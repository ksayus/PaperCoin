using System.Globalization;
using System.Text;
using PaperCoin.Core.Models;
using PaperCoin.Service;
using PaperCoin.Service.LanSync;

namespace PaperCoin.SyncTests;

/// <summary>
/// 协议 v1 的独立测试控制台。不依赖 MSTest / xUnit，直接 dotnet run；
/// 任何一条失败都会让进程以非零码退出。
/// </summary>
internal static class Program
{
    private const string IdA = "11111111-1111-4111-8111-111111111111";
    private const string IdB = "22222222-2222-4222-8222-222222222222";
    private const string IdC = "33333333-3333-4333-8333-333333333333";

    private static int _failures;
    private static int _passes;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Check("1. 内容哈希测试向量", TestContentHashVectors);
        Check("2. 快照哈希测试向量", TestSnapshotVector);
        Check("3. HMAC 测试向量与常量时间比较", TestHmacVector);
        Check("4. 金额 / 分类规范化", TestCanonicalization);
        Check("5. 双方都改动 -> 冲突", TestConflictBothChanged);
        Check("6. 删除 vs 改动", TestDeleteVsChange);
        Check("7. 日期移动", TestDateMove);
        Check("8. 动作生成与基线更新", TestActionsAndBaseline);
        Check("9. 线缆帧往返", TestFraming);
        await Check("10. 状态文件读写", TestStateStoreAsync);
        await Check("11. BillRepository 原子 apply 与并发保护", TestRepositoryApplyAsync);
        await Check("12. 端到端 TCP 会话 / 冲突 abort / 收敛", TestEndToEndAsync);
        Check("13. host:port 解析", TestEndpointParsing);
        Check("14. 同步码生成", TestGenerateCode);

        Console.WriteLine("========================================");
        Console.WriteLine($"通过 {_passes} 项，失败 {_failures} 项");
        Console.WriteLine("========================================");

        return _failures == 0 ? 0 : 1;
    }

    // ---- 1 ----

    private static void TestContentHashVectors()
    {
        var lunch = Entry(IdA, "午饭", -12.5m, "餐饮", "2026-10-18");
        var subway = Entry(IdB, "地铁", -6m, "交通", "2026-10-18");

        Equal("5015e4ccef8f9565d925127cc5b483b831f03bad2dab6bf0fb9482fa35f75593",
            LanSyncHash.ContentHash(lunch), "午饭 hash");

        Equal("9e3182d6536d646424e7ca3239c1bdd201b130964500a0ca1170bd9553e5c7c9",
            LanSyncHash.ContentHash(subway), "地铁 hash");
    }

    // ---- 2 ----

    private static void TestSnapshotVector()
    {
        var lunch = Entry(IdA, "午饭", -12.5m, "餐饮", "2026-10-18");
        var subway = Entry(IdB, "地铁", -6m, "交通", "2026-10-18");

        var snapshot = SyncSnapshot.Create([lunch, subway]);

        Equal("0f3fee08b5b1ad19bdd72eabd7b8f16df60b36f7024a4d0228b04e84aa17e6c4",
            snapshot.Hash, "两条账单的快照 hash");

        Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            SyncSnapshot.Empty.Hash, "空快照 hash（空字符串的 SHA-256）");
    }

    // ---- 3 ----

    private static void TestHmacVector()
    {
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var mac = LanSyncHash.ComputeAuthMac("ABCD2345", nonce, "device-1");

        Equal("0889e7bae7ad9f5038aad194514aeb8a3195e1c4f83f8f9abc7e70c4b7198a08",
            mac, "HMAC test vector");

        Require(LanSyncHash.FixedTimeEqualsHex(mac, mac.ToUpperInvariant()), "十六进制大小写应视为同一个 MAC");
        Require(!LanSyncHash.FixedTimeEqualsHex(mac, new string('0', 64)), "不同 MAC 不能相等");
        Require(!LanSyncHash.FixedTimeEqualsHex(mac, "zz"), "非法十六进制必须返回 false");
    }

    // ---- 4 ----

    private static void TestCanonicalization()
    {
        Equal("1.01", LanSyncHash.FormatAmount(1.005m), "1.005 向远离零舍入");
        Equal("-1.01", LanSyncHash.FormatAmount(-1.005m), "-1.005 向远离零舍入");
        Equal("1.00", LanSyncHash.FormatAmount(1.004m), "1.004 舍入");
        Equal("0.00", LanSyncHash.FormatAmount(-0.004m), "负零写成 0.00");
        Equal("12000.00", LanSyncHash.FormatAmount(12000m), "整数补两位");

        var trimmed = Entry(IdA, "午饭", -12.5m, " 餐饮 ", "2026-10-18");
        var plain = Entry(IdA, "午饭", -12.5m, "餐饮", "2026-10-18");
        var nullCategory = Entry(IdA, "午饭", -12.5m, null, "2026-10-18");
        var emptyCategory = Entry(IdA, "午饭", -12.5m, "", "2026-10-18");

        Equal(LanSyncHash.ContentHash(plain), LanSyncHash.ContentHash(trimmed), "分类要 Trim");
        Equal(LanSyncHash.ContentHash(emptyCategory), LanSyncHash.ContentHash(nullCategory),
            "null 分类等价于空分类");

        var spacedDescription = Entry(IdA, "午饭 ", -12.5m, "餐饮", "2026-10-18");
        Require(LanSyncHash.ContentHash(plain) != LanSyncHash.ContentHash(spacedDescription),
            "描述必须原样参与哈希，不能 Trim");
    }

    // ---- 5 ----

    private static void TestConflictBothChanged()
    {
        var baseline = Baseline(Entry(IdA, "base", -1m, "餐饮", "2026-10-18"));
        var local = Entry(IdA, "local", -2m, "餐饮", "2026-10-18");
        var remote = Entry(IdA, "remote", -3m, "餐饮", "2026-10-18");

        var l = SyncSnapshot.Create([local]);
        var r = SyncSnapshot.Create([remote]);

        var unresolved = MergeEngine.Merge(l, r, baseline);
        Require(!unresolved.IsComplete, "双方都改动时必须冲突");
        Require(unresolved.Conflicts.Count == 1, "应有一处冲突");
        Require(unresolved.Conflicts[0].Local?.Description == "local", "冲突应保留本机内容");
        Require(unresolved.Conflicts[0].Remote?.Description == "remote", "冲突应保留对方内容");
        Equal("", unresolved.FinalSnapshotHash, "未解决冲突不能产生最终 hash");
        Require(unresolved.LocalActions.Count == 0 && unresolved.RemoteActions.Count == 0,
            "未解决冲突不能产生动作");

        var keepLocal = MergeEngine.Merge(l, r, baseline, Choices(IdA, ConflictChoice.Local));
        Require(keepLocal.IsComplete, "选择本机后应完成");
        Equal(local.Description, keepLocal.Final.Find(IdA)?.Description, "最终取本机");
        Require(keepLocal.LocalActions.Count == 0, "本机等于最终，不应有动作");
        Require(keepLocal.RemoteActions.Count == 1
                && keepLocal.RemoteActions[0].Op == SyncActionOp.Upsert,
            "对方应收到一条 upsert");

        var keepRemote = MergeEngine.Merge(l, r, baseline, Choices(IdA, ConflictChoice.Remote));
        Require(keepRemote.IsComplete, "选择对方后应完成");
        Equal(remote.Description, keepRemote.Final.Find(IdA)?.Description, "最终取对方");
        Require(keepRemote.RemoteActions.Count == 0, "对方等于最终，不应有动作");
        Require(keepRemote.LocalActions.Count == 1
                && keepRemote.LocalActions[0].Op == SyncActionOp.Upsert,
            "本机应收到一条 upsert");
    }

    // ---- 6 ----

    private static void TestDeleteVsChange()
    {
        var baselineEntry = Entry(IdA, "base", -1m, "餐饮", "2026-10-18");
        var baseline = Baseline(baselineEntry);

        // 本机改了，对方删了
        var localChanged = Entry(IdA, "changed", -2m, "餐饮", "2026-10-18");
        var l = SyncSnapshot.Create([localChanged]);
        var empty = SyncSnapshot.Empty;

        var conflict = MergeEngine.Merge(l, empty, baseline);
        Require(!conflict.IsComplete, "本机改动 + 对方删除必须冲突");
        Require(conflict.Conflicts[0].RemoteDeleted, "冲突应标记对方已删除");

        var keepLocal = MergeEngine.Merge(l, empty, baseline, Choices(IdA, ConflictChoice.Local));
        Require(keepLocal.IsComplete, "选择本机后应完成");
        Equal(localChanged.Description, keepLocal.Final.Find(IdA)?.Description, "最终保留本机改动");
        Require(keepLocal.RemoteActions.Count == 1
                && keepLocal.RemoteActions[0].Op == SyncActionOp.Upsert,
            "对方的删除应被 upsert 覆盖");

        var keepRemote = MergeEngine.Merge(l, empty, baseline, Choices(IdA, ConflictChoice.Remote));
        Require(keepRemote.IsComplete, "选择对方后应完成");
        Require(keepRemote.Final.Find(IdA) is null, "最终应是删除");
        Require(keepRemote.LocalActions.Count == 1
                && keepRemote.LocalActions[0].Op == SyncActionOp.Delete
                && keepRemote.LocalActions[0].Date == "2026-10-18",
            "本机应收到删除动作");

        // 本机删了，对方改了
        var remoteChanged = Entry(IdA, "remote changed", -3m, "餐饮", "2026-10-18");
        var r = SyncSnapshot.Create([remoteChanged]);

        var conflict2 = MergeEngine.Merge(empty, r, baseline);
        Require(!conflict2.IsComplete, "本机删除 + 对方改动必须冲突");
        Require(conflict2.Conflicts[0].LocalDeleted, "冲突应标记本机已删除");

        var keepRemote2 = MergeEngine.Merge(empty, r, baseline, Choices(IdA, ConflictChoice.Remote));
        Require(keepRemote2.IsComplete, "选择对方后应完成");
        Equal("remote changed", keepRemote2.Final.Find(IdA)?.Description, "最终保留对方改动");
        Require(keepRemote2.LocalActions.Count == 1
                && keepRemote2.LocalActions[0].Op == SyncActionOp.Upsert,
            "本机应收到 upsert");

        var keepLocal2 = MergeEngine.Merge(empty, r, baseline, Choices(IdA, ConflictChoice.Local));
        Require(keepLocal2.IsComplete, "选择本机后应完成");
        Require(keepLocal2.Final.Find(IdA) is null, "最终应是删除");
        Require(keepLocal2.RemoteActions.Count == 1
                && keepLocal2.RemoteActions[0].Op == SyncActionOp.Delete,
            "对方应收到删除动作");
    }

    // ---- 7 ----

    private static void TestDateMove()
    {
        var baselineEntry = Entry(IdA, "move", -10m, "交通", "2026-10-18");
        var baseline = Baseline(baselineEntry);

        var localMoved = Entry(IdA, "move", -10m, "交通", "2026-10-19");
        var remoteOld = baselineEntry;

        var l = SyncSnapshot.Create([localMoved]);
        var r = SyncSnapshot.Create([remoteOld]);

        var merge = MergeEngine.Merge(l, r, baseline);
        Require(merge.IsComplete, "只有本机改日期时不应冲突");
        Equal("2026-10-19", merge.Final.Find(IdA)?.Date, "最终取本机新日期");
        Require(merge.LocalActions.Count == 0, "本机是最终状态，不应有动作");

        Require(merge.RemoteActions.Count == 2, "对方需要先删旧日期再写新日期");
        Require(merge.RemoteActions[0].Op == SyncActionOp.Delete
                && merge.RemoteActions[0].Date == "2026-10-18",
            "第一条必须是删除旧日期");
        Require(merge.RemoteActions[1].Op == SyncActionOp.Upsert
                && merge.RemoteActions[1].Entry?.Date == "2026-10-19",
            "第二条必须是 upsert 新日期");

        // 反向：对方移动日期，本机没动
        var reverse = MergeEngine.Merge(r, l, baseline);
        Require(reverse.IsComplete, "反向日期移动不应冲突");
        Equal("2026-10-19", reverse.Final.Find(IdA)?.Date, "反向最终取对方新日期");
        Require(reverse.RemoteActions.Count == 0, "对方是最终状态，不应有动作");
        Require(reverse.LocalActions.Count == 2
                && reverse.LocalActions[0].Op == SyncActionOp.Delete
                && reverse.LocalActions[1].Op == SyncActionOp.Upsert,
            "本机必须先删旧日期再写新日期");
    }

    // ---- 8 ----

    private static void TestActionsAndBaseline()
    {
        var a = Entry(IdA, "本机新增", -5m, "餐饮", "2026-10-18");
        var b = Entry(IdB, "对方新增", 100m, "工资", "2026-10-18");

        var l = SyncSnapshot.Create([a]);
        var r = SyncSnapshot.Create([b]);

        var merge = MergeEngine.Merge(l, r, EmptyBaseline());
        Require(merge.IsComplete, "双方各自新增不应冲突");
        Require(merge.Final.Count == 2, "最终应包含两条");
        Require(merge.LocalActions.Count == 1
                && merge.LocalActions[0].Op == SyncActionOp.Upsert
                && merge.LocalActions[0].Entry?.Id == IdB,
            "本机应补上对方的新增");
        Require(merge.RemoteActions.Count == 1
                && merge.RemoteActions[0].Op == SyncActionOp.Upsert
                && merge.RemoteActions[0].Entry?.Id == IdA,
            "对方应补上本机的新增");

        var baseline = MergeEngine.BaselineMap(merge.Final);
        Require(baseline.Count == 2, "基线应覆盖最终全部条目");
        Require(baseline.ContainsKey(IdA) && baseline.ContainsKey(IdB), "基线键应完整");
        Equal(LanSyncHash.ContentHash(a), baseline[IdA], "基线值应为内容哈希");

        var items = MergeEngine.BaselineItems(merge.Final);
        Require(items.Count == 2 && items[0].Id == IdA && items[1].Id == IdB,
            "线缆基线必须按 Id 序数排序");

        // 两边完全相同：不应产生任何动作
        var same = MergeEngine.Merge(l, l, EmptyBaseline());
        Require(same.IsComplete, "相同快照不应冲突");
        Require(same.LocalActions.Count == 0 && same.RemoteActions.Count == 0,
            "相同快照不应产生动作");

        // 对方删除、本机未改
        var baselineA = Baseline(a);
        var remoteDeleted = MergeEngine.Merge(l, SyncSnapshot.Empty, baselineA);
        Require(remoteDeleted.IsComplete, "对方删除 + 本机未改应自动合并");
        Require(remoteDeleted.Final.Find(IdA) is null, "最终应删除");
        Require(remoteDeleted.LocalActions.Count == 1
                && remoteDeleted.LocalActions[0].Op == SyncActionOp.Delete,
            "本机应收到删除动作");

        // 本机删除、对方未改
        var localDeleted = MergeEngine.Merge(SyncSnapshot.Empty, r, Baseline(b));
        Require(localDeleted.IsComplete, "本机删除 + 对方未改应自动合并");
        Require(localDeleted.Final.Find(IdB) is null, "最终应删除");
        Require(localDeleted.RemoteActions.Count == 1
                && localDeleted.RemoteActions[0].Op == SyncActionOp.Delete,
            "对方应收到删除动作");
    }

    // ---- 9 ----

    private static void TestFraming()
    {
        var entry = Entry(IdA, "午饭", -12.5m, "餐饮", "2026-10-18");
        var snapshot = SyncSnapshot.Create([entry]);
        var message = SyncMessage.Snapshot(snapshot);

        using var stream = new MemoryStream();

        SyncFraming.WriteAsync(stream, message, CancellationToken.None).GetAwaiter().GetResult();
        stream.Position = 0;

        var read = SyncFraming.ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
        Equal("snapshot", read.Type, "帧类型");
        Equal(snapshot.Hash, read.Hash, "帧内 hash");
        Require(WireConvert.TryToSnapshot(read, out var decoded, out var error), error);
        Equal(snapshot.Hash, decoded.Hash, "解码后快照 hash");

        // 4 字节大端长度前缀
        stream.Position = 0;
        var header = new byte[4];
        stream.ReadExactly(header);
        int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        Require(length > 0 && length < SyncFraming.MaxFrameBytes, "长度前缀应是大端且在限制内");

        // 超长帧头必须在不分配 8MiB 以上内存的情况下被拒绝
        using var huge = new MemoryStream([0x7F, 0xFF, 0xFF, 0xFF]);
        try
        {
            SyncFraming.ReadAsync(huge, CancellationToken.None).GetAwaiter().GetResult();
            throw new TestFailure("超长帧应被拒绝");
        }
        catch (InvalidDataException)
        {
            // 预期
        }
    }

    // ---- 10 ----

    private static async Task TestStateStoreAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "PaperCoinSyncTests", Guid.NewGuid().ToString("N"),
            "papercoin-sync.json");

        try
        {
            var store = new SyncStore(path);

            var state = new SyncState
            {
                Enabled = true,
                AutoSync = true,
                Code = "abcd2345",
                DeviceId = IdA,
                DeviceName = "  DESKTOP  ",
                Peers =
                [
                    new SyncPeer
                    {
                        DeviceId = IdB,
                        DeviceName = "Pixel 8",
                        LastSyncAt = 1792250400000,
                        Baseline = new Dictionary<string, string>
                        {
                            [IdA] = LanSyncHash.ContentHash(
                                Entry(IdA, "午饭", -12.5m, "餐饮", "2026-10-18"))
                        }
                    }
                ]
            };

            await store.SaveAsync(state);
            Require(File.Exists(path), "状态文件应写盘");

            var loaded = await store.LoadAsync();
            Require(loaded.Enabled, "enabled 应保留");
            Equal("ABCD2345", loaded.Code, "code 应转大写");
            Equal("DESKTOP", loaded.DeviceName, "deviceName 应 Trim");
            Equal(IdA, loaded.DeviceId, "deviceId 应归一化");
            Require(loaded.Peers.Count == 1, "peers 应保留");
            Equal("Pixel 8", loaded.Peers[0].DeviceName, "peer 名称");
            Equal(1792250400000, loaded.Peers[0].LastSyncAt, "peer lastSyncAt");
            Require(loaded.Peers[0].Baseline.Count == 1, "baseline 应保留");

            // code 为空 -> 协议规定同步关闭
            var disabled = SyncStore.Normalize(new SyncState { Enabled = true, Code = "" });
            Require(!disabled.Enabled, "空 code 必须强制关闭同步");

            // 非法 code -> 清空
            var invalid = SyncStore.Normalize(new SyncState { Enabled = true, Code = "abc" });
            Equal("", invalid.Code, "太短的 code 应被清空");
            Require(!invalid.Enabled, "非法 code 必须关闭同步");
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    // ---- 11 ----

    private static async Task TestRepositoryApplyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperCoinSyncTests", "bills", Guid.NewGuid().ToString("N"));

        try
        {
            var repo = new BillRepository(root);
            var oldDate = new DateOnly(2026, 10, 18);
            var newDate = new DateOnly(2026, 10, 19);

            var a = new BillEntry
            {
                Id = Guid.Parse(IdA),
                Description = "搬家前",
                Amount = -10m,
                Category = "交通",
                Date = oldDate
            };

            var b = new BillEntry
            {
                Id = Guid.Parse(IdB),
                Description = "午饭",
                Amount = -12.5m,
                Category = "餐饮",
                Date = oldDate
            };

            await repo.UpsertEntryAsync(a);
            await repo.UpsertEntryAsync(b);

            var before = await repo.ReadSnapshotAsync();

            var aMoved = new BillEntry
            {
                Id = Guid.Parse(IdA),
                Description = "搬家后",
                Amount = -11m,
                Category = "交通",
                Date = newDate
            };

            var c = new BillEntry
            {
                Id = Guid.Parse(IdC),
                Description = "新增",
                Amount = 100m,
                Category = "工资",
                Date = oldDate
            };

            var final = SyncSnapshot.Create(
                [SyncEntry.FromBillEntry(aMoved), SyncEntry.FromBillEntry(b), SyncEntry.FromBillEntry(c)]);

            var actions = new List<SyncAction>
            {
                SyncAction.Delete(IdA, "2026-10-18"),
                SyncAction.Upsert(SyncEntry.FromBillEntry(aMoved)),
                SyncAction.Upsert(SyncEntry.FromBillEntry(c))
            };

            var result = await repo.ApplyChangesAsync(before.Hash, actions, final.Hash);
            Require(result.IsSuccess, $"apply 应成功: {result.Status} {result.Message}");

            var after = await repo.ReadSnapshotAsync();
            Equal(final.Hash, after.Hash, "apply 后快照应等于最终快照");
            Require(after.Count == 3, "apply 后应有 3 条");
            Equal("2026-10-19", after.Find(IdA)?.Date, "日期应移动");

            // 交换快照之后用户又编辑：旧 expected hash 必须被拒绝，且一个字节都不写。
            var beforeConcurrentEdit = await repo.ReadSnapshotAsync();

            var bEdited = new BillEntry
            {
                Id = Guid.Parse(IdB),
                Description = "用户刚改的",
                Amount = -13m,
                Category = "餐饮",
                Date = oldDate
            };

            await repo.UpsertEntryAsync(bEdited, previousDate: oldDate);

            var afterEdit = await repo.ReadSnapshotAsync();
            Require(afterEdit.Hash != beforeConcurrentEdit.Hash, "并发编辑应改变快照");

            var stale = await repo.ApplyChangesAsync(
                beforeConcurrentEdit.Hash,
                [SyncAction.Delete(IdA, "2026-10-19")],
                new string('a', 64));

            Require(stale.Status == BillApplyStatus.SnapshotChanged,
                $"过期 expected hash 必须返回 SnapshotChanged，实际 {stale.Status}");

            var afterStale = await repo.ReadSnapshotAsync();
            Equal(afterEdit.Hash, afterStale.Hash, "被拒绝的 apply 不能部分写入");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- 12 ----

    private static async Task TestEndToEndAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperCoinSyncTests", "e2e", Guid.NewGuid().ToString("N"));
        var serverRoot = Path.Combine(root, "server");
        var clientRoot = Path.Combine(root, "client");

        try
        {
            var serverRepo = new BillRepository(serverRoot);
            var clientRepo = new BillRepository(clientRoot);

            var sharedId = Guid.Parse(IdB);
            var onlyServerId = Guid.Parse(IdA);
            var onlyClientId = Guid.Parse(IdC);
            var date = new DateOnly(2026, 10, 18);

            var shared = new BillEntry
            {
                Id = sharedId,
                Description = "共享",
                Amount = -5m,
                Category = "餐饮",
                Date = date
            };

            var onlyServer = new BillEntry
            {
                Id = onlyServerId,
                Description = "只在本机(服务端)",
                Amount = 100m,
                Category = "工资",
                Date = date
            };

            var onlyClient = new BillEntry
            {
                Id = onlyClientId,
                Description = "只在客户端",
                Amount = -8m,
                Category = "交通",
                Date = date
            };

            await serverRepo.UpsertEntryAsync(shared);
            await serverRepo.UpsertEntryAsync(onlyServer);
            await clientRepo.UpsertEntryAsync(shared);
            await clientRepo.UpsertEntryAsync(onlyClient);

            var serverIdentity = new LanSyncIdentity(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "SERVER", "ABCD2345");

            PeerAppliedInfo? applied = null;

            await using var server = new LanSyncServer(
                serverRepo,
                () => serverIdentity,
                info =>
                {
                    applied = info;
                    return Task.CompletedTask;
                });

            await server.StartAsync(0);

            var client = new LanSyncClient(clientRepo);
            var emptyBaseline = new Dictionary<string, string>(StringComparer.Ordinal);

            var options = new LanSyncClientOptions(
                "127.0.0.1",
                server.Port,
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                "CLIENT",
                "ABCD2345",
                0,
                emptyBaseline,
                BaselineLookup: _ => emptyBaseline);

            var first = await client.SyncAsync(options);
            Require(first.Success, $"首次同步失败: {first.ErrorCode} {first.ErrorMessage}");
            Equal(serverIdentity.DeviceId, first.ServerDeviceId, "应识别服务端设备 Id");

            var serverSnapshot = await serverRepo.ReadSnapshotAsync();
            var clientSnapshot = await clientRepo.ReadSnapshotAsync();
            Equal(serverSnapshot.Hash, clientSnapshot.Hash, "首次同步后两端快照应由同一 hash 描述");
            Require(serverSnapshot.Count == 3, $"首次同步后应有 3 条，实际 {serverSnapshot.Count}");
            Require(applied is not null, "服务端应收到 applied 回调");
            Equal(3, applied!.Baseline.Count, "服务端基线应覆盖最终全部条目");

            // 第二次：两边已经一致，不应再产生动作。
            var secondOptions = options with
            {
                Baseline = first.Baseline,
                BaselineLookup = _ => first.Baseline
            };

            var second = await client.SyncAsync(secondOptions);
            Require(second.Success, $"第二次同步失败: {second.ErrorCode} {second.ErrorMessage}");
            Equal(0, second.AppliedCount, "一致时服务端不应收到动作");
            Equal(0, second.LocalActionCount, "一致时本机不应产生动作");

            // 双方改同一条 -> 客户端必须 abort，服务端数据保持原样。
            await serverRepo.UpsertEntryAsync(new BillEntry
            {
                Id = sharedId,
                Description = "服务端修改",
                Amount = -5m,
                Category = "餐饮",
                Date = date
            });

            await clientRepo.UpsertEntryAsync(new BillEntry
            {
                Id = sharedId,
                Description = "客户端修改",
                Amount = -5m,
                Category = "餐饮",
                Date = date
            });

            var conflictOptions = options with
            {
                Baseline = second.Baseline,
                BaselineLookup = _ => second.Baseline
            };

            var conflict = await client.SyncAsync(conflictOptions);
            Require(conflict.HasConflicts && !conflict.Success, "双方改同一条必须返回冲突");
            Require(conflict.Conflicts.Count(c => !c.IsResolved) == 1, "应有一处未解决冲突");
            Equal("", conflict.FinalSnapshotHash, "冲突会话不能有最终 hash");

            var serverAfterAbort = await serverRepo.ReadSnapshotAsync();
            Equal("服务端修改",
                serverAfterAbort.Find(sharedId.ToString("D"))?.Description,
                "冲突 abort 后服务端不得被修改");

            var clientAfterAbort = await clientRepo.ReadSnapshotAsync();
            Equal("客户端修改",
                clientAfterAbort.Find(sharedId.ToString("D"))?.Description,
                "冲突 abort 后客户端不得被修改");

            // 选择保留对方，重连后两端收敛。
            var choices = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal)
            {
                [sharedId.ToString("D")] = ConflictChoice.Remote
            };

            var retry = await client.SyncAsync(conflictOptions with
            {
                Choices = choices,
                ChoicesLookup = _ => choices
            });

            Require(retry.Success, $"按选择重试失败: {retry.ErrorCode} {retry.ErrorMessage}");

            var finalServer = await serverRepo.ReadSnapshotAsync();
            var finalClient = await clientRepo.ReadSnapshotAsync();
            Equal(finalServer.Hash, finalClient.Hash, "冲突解决后两端应一致");
            Equal("服务端修改",
                finalClient.Find(sharedId.ToString("D"))?.Description,
                "选择 REMOTE 后本机应接受对方内容");

            // 同步码错误必须被服务端拒绝，且不改数据。
            var bad = await client.SyncAsync(options with
            {
                Code = "ZZZZZZZZ",
                Baseline = retry.Baseline,
                BaselineLookup = _ => retry.Baseline
            });

            Require(!bad.Success, "错误同步码不能成功");
            Equal("auth", bad.ErrorCode, "错误同步码应返回 auth");

            var afterBad = await serverRepo.ReadSnapshotAsync();
            Equal(finalServer.Hash, afterBad.Hash, "认证失败不能改服务端数据");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- 13 ----

    private static void TestEndpointParsing()
    {
        Require(LanSyncService.TryParseEndpoint("192.168.1.20:47832", out var host, out var port, out _),
            "host:port 应解析成功");
        Equal("192.168.1.20", host, "host");
        Equal(47832, port, "port");

        Require(LanSyncService.TryParseEndpoint("desktop.local", out host, out port, out _),
            "只有 host 时应使用默认端口");
        Equal("desktop.local", host, "默认 host");
        Equal(47832, port, "默认 port");

        Require(LanSyncService.TryParseEndpoint("[::1]:50000", out host, out port, out _),
            "IPv6 方括号格式应解析成功");
        Equal("::1", host, "IPv6 host");
        Equal(50000, port, "IPv6 port");

        Require(!LanSyncService.TryParseEndpoint("host:notaport", out _, out _, out _),
            "非法端口应失败");
        Require(!LanSyncService.TryParseEndpoint("host:70000", out _, out _, out _),
            "越界端口应失败");
        Require(!LanSyncService.TryParseEndpoint("   ", out _, out _, out _),
            "空地址应失败");
    }

    // ---- 13 ----

    private static void TestGenerateCode()
    {
        for (int i = 0; i < 100; i++)
        {
            var code = LanSyncService.GenerateCode();
            Require(code.Length is >= 6 and <= 12, "同步码长度");
            Require(code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'),
                "同步码字符集必须是大写 A-Z / 0-9");
        }
    }

    // ---- 工具 ----

    private static SyncEntry Entry(string id, string description, decimal amount, string? category, string date)
        => new()
        {
            Id = id,
            Description = description,
            Amount = amount,
            Category = category,
            Date = date
        };

    private static Dictionary<string, string> Baseline(params SyncEntry[] entries)
        => MergeEngine.BaselineMap(SyncSnapshot.Create(entries));

    private static Dictionary<string, string> EmptyBaseline()
        => new(StringComparer.Ordinal);

    private static Dictionary<string, ConflictChoice> Choices(string id, ConflictChoice choice)
        => new(StringComparer.Ordinal) { [id] = choice };

    private static void Check(string name, Action body)
    {
        Console.WriteLine($"[测试] {name}");

        try
        {
            body();
            _passes++;
            Console.WriteLine("  结果: OK");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  结果: 失败 -> {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Console.WriteLine();
        }
    }

    private static async Task Check(string name, Func<Task> body)
    {
        Console.WriteLine($"[测试] {name}");

        try
        {
            await body();
            _passes++;
            Console.WriteLine("  结果: OK");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  结果: 失败 -> {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Console.WriteLine();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new TestFailure(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new TestFailure($"{message}: 期望 {expected}, 实际 {actual}");
    }

    private sealed class TestFailure : Exception
    {
        public TestFailure(string message) : base(message)
        {
        }
    }
}
