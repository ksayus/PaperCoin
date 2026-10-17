package com.ksayus.papercoin.data

import android.content.Context
import android.os.Build
import com.ksayus.papercoin.sync.SyncManager
import com.ksayus.papercoin.sync.SyncStore
import com.ksayus.papercoin.ui.theme.ThemeState
import java.io.File

/**
 * 手工依赖容器。这个程序只有一个进程、一个用户，不需要 Hilt 的构建期开销；
 * 仓储都持有内存索引或单例状态，因此必须是单例。
 */
class AppContainer(context: Context) {
    private val appContext = context.applicationContext

    /** 应用私有数据根：/data/user/0/<包名>/files/PaperCoin */
    val dataRoot: File = File(appContext.filesDir, "PaperCoin")

    val repository: BillRepository = BillRepository(File(dataRoot, "Bills"))

    val config: ConfigRepository =
        ConfigRepository(File(dataRoot, "Config/papercoin-settings.json"))

    val theme: ThemeState = ThemeState(config)

    val backup: BackupManager = BackupManager(appContext, repository, dataRoot)

    val updates: UpdateManager = UpdateManager(appContext, config)

    val syncStore: SyncStore =
        SyncStore(File(dataRoot, "Sync/papercoin-sync.json"), Build.MODEL)

    val sync: SyncManager = SyncManager(repository, syncStore)
}
