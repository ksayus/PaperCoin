package com.ksayus.papercoin.data

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import com.ksayus.papercoin.BuildConfig
import com.ksayus.papercoin.core.AppLog
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.SocketTimeoutException
import java.net.URL

enum class UpdateSource { GITHUB, GITEE }

data class SemanticVersion(
    val major: Int,
    val minor: Int,
    val patch: Int,
) : Comparable<SemanticVersion> {
    override fun compareTo(other: SemanticVersion): Int =
        compareValuesBy(this, other, { it.major }, { it.minor }, { it.patch })

    override fun toString(): String = "$major.$minor.$patch"

    companion object {
        /** 接受 v1.3.6 / 1.3.6 / 1.3；不足三段补零，-beta / +build 后缀忽略。 */
        fun parse(tag: String?): SemanticVersion? {
            if (tag.isNullOrBlank()) return null

            var text = tag.trim().trimStart('v', 'V')

            val cut = text.indexOfFirst { it == '-' || it == '+' }
            if (cut >= 0) text = text.substring(0, cut)

            if (text.isBlank()) return null

            val parts = text.split('.')
            val numbers = parts.take(3).map { it.toIntOrNull() ?: return null }
            if (numbers.any { it < 0 }) return null

            return SemanticVersion(
                numbers.getOrElse(0) { 0 },
                numbers.getOrElse(1) { 0 },
                numbers.getOrElse(2) { 0 },
            )
        }
    }
}

data class UpdateRelease(
    val version: SemanticVersion,
    val tagName: String,
    val notes: String,
    val pageUrl: String,
    val installerUrl: String?,
    val installerName: String?,
)

data class UpdateCheck(
    val release: UpdateRelease?,
    val message: String,
    val isFailure: Boolean,
)

/** 一次版本查询与 APK 下载。任何失败都收敛成一句能显示给用户的话，绝不抛到界面。 */
class UpdateManager(
    private val context: Context,
    private val config: ConfigRepository,
) {
    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    private val _available = MutableStateFlow<UpdateRelease?>(null)
    val available: StateFlow<UpdateRelease?> = _available.asStateFlow()

    private val _lastMessage = MutableStateFlow("")
    val lastMessage: StateFlow<String> = _lastMessage.asStateFlow()

    private var autoCheckStarted = false

    fun currentVersion(): SemanticVersion =
        SemanticVersion.parse(BuildConfig.VERSION_NAME) ?: SemanticVersion(0, 0, 0)

    fun sourceOf(text: String?): UpdateSource =
        if (text.equals("Gitee", ignoreCase = true)) UpdateSource.GITEE else UpdateSource.GITHUB

    /** 启动时查一次；重复进入组合不会重复联网。 */
    fun autoCheckOnce(scope: CoroutineScope) {
        if (autoCheckStarted) return
        autoCheckStarted = true

        val app = config.config.value.app
        if (!app.autoUpdate) return

        scope.launch {
            runCatching { checkAndRemember(sourceOf(app.autoUpdateSource), currentVersion()) }
        }
    }

    suspend fun checkAndRemember(source: UpdateSource, current: SemanticVersion): UpdateCheck {
        val check = check(source, current)

        _lastMessage.value = check.message
        _available.value = check.release?.takeIf { it.version > current }

        return check
    }

    suspend fun check(source: UpdateSource, current: SemanticVersion): UpdateCheck =
        withContext(Dispatchers.IO) {
            val result = httpGet(endpointOf(source))

            if (result.error is SocketTimeoutException) {
                return@withContext UpdateCheck(null, "查询超时，检查网络后重试", true)
            }

            if (result.error != null) {
                return@withContext UpdateCheck(
                    null,
                    "连不上${sourceName(source)}（${result.error.message}）",
                    true,
                )
            }

            when (result.code) {
                404 -> return@withContext UpdateCheck(null, "还没有发布过任何版本", false)
                403, 429 -> return@withContext UpdateCheck(
                    null,
                    "查询被拒绝（HTTP ${result.code}），稍后再试",
                    true,
                )
            }

            if (result.code !in 200..299) {
                return@withContext UpdateCheck(null, "查询失败（HTTP ${result.code}）", true)
            }

            val release = try {
                parseRelease(result.body)
            } catch (ex: ParseFailure) {
                return@withContext UpdateCheck(null, ex.message.orEmpty(), true)
            } catch (ex: Exception) {
                return@withContext UpdateCheck(null, "更新源返回的内容看不懂（不是合法 JSON）", true)
            }

            if (release.version > current) {
                UpdateCheck(release, "发现新版本 ${release.version}（当前 $current）", false)
            } else {
                UpdateCheck(release, "已是最新版本（$current）", false)
            }
        }

    /**
     * 下载 APK 到应用缓存。返回 (文件, 失败原因)；失败原因为空表示成功。
     * 安装交给系统安装器，这里不替用户关掉程序。
     */
    suspend fun download(
        release: UpdateRelease,
        onProgress: (Long, Long) -> Unit,
    ): Pair<File?, String> = withContext(Dispatchers.IO) {
        val url = release.installerUrl
            ?: return@withContext Pair(null, "这个发布里没有安卓安装包")

        val dir = File(context.cacheDir, "updates")
        dir.mkdirs()

        val target = File(dir, safeFileName(release.installerName))
        var connection: HttpURLConnection? = null

        try {
            connection = (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = 20_000
                readTimeout = 60_000
                instanceFollowRedirects = true
                setRequestProperty("User-Agent", "PaperCoin/${BuildConfig.VERSION_NAME}")
            }

            val code = connection.responseCode
            if (code !in 200..299) {
                return@withContext Pair(null, "下载失败（HTTP $code）")
            }

            val total = connection.contentLengthLong
            var done = 0L

            connection.inputStream.use { input ->
                target.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)

                    while (true) {
                        val read = input.read(buffer)
                        if (read <= 0) break

                        output.write(buffer, 0, read)
                        done += read
                        onProgress(done, total)
                    }
                }
            }

            Pair(target, "")
        } catch (ex: SocketTimeoutException) {
            target.delete()
            Pair(null, "下载超时，检查网络后重试")
        } catch (ex: IOException) {
            target.delete()
            Pair(null, "下载失败（${ex.message}）")
        } finally {
            connection?.disconnect()
        }
    }

    /**
     * 交给系统安装器。Android 上静默安装不允许，必须由用户点确认；
     * 未授权"安装未知来源应用"时先把用户送到对应的系统设置页。
     */
    fun install(file: File): String {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
            !context.packageManager.canRequestPackageInstalls()
        ) {
            return try {
                val intent = Intent(
                    Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:${context.packageName}"),
                ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

                context.startActivity(intent)
                "请在系统设置里允许 PaperCoin 安装未知来源应用，然后重新点“下载并安装”"
            } catch (ex: Exception) {
                "当前系统不允许安装未知来源应用，请到设置里允许后重试"
            }
        }

        val uri = try {
            FileProvider.getUriForFile(
                context,
                "${context.packageName}.fileprovider",
                file,
            )
        } catch (ex: IllegalArgumentException) {
            AppLog.warn("安装包路径无效", ex)
            return "安装包路径无效"
        }

        val intent = Intent(Intent.ACTION_VIEW)
            .setDataAndType(uri, "application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)

        return try {
            context.startActivity(intent)
            ""
        } catch (ex: ActivityNotFoundException) {
            "无法启动安装程序"
        }
    }

    // ---- 网络 ----

    private data class HttpResult(
        val code: Int,
        val body: String,
        val error: IOException?,
    )

    private fun httpGet(url: String): HttpResult {
        var connection: HttpURLConnection? = null

        try {
            connection = (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = 15_000
                readTimeout = 20_000
                instanceFollowRedirects = true
                // GitHub 要求带 User-Agent，否则直接 403
                setRequestProperty("User-Agent", "PaperCoin/${BuildConfig.VERSION_NAME}")
                setRequestProperty("Accept", "application/vnd.github+json")
            }

            val code = connection.responseCode
            val body = if (code in 200..299) {
                connection.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
            } else {
                ""
            }

            return HttpResult(code, body, null)
        } catch (ex: SocketTimeoutException) {
            return HttpResult(-1, "", ex)
        } catch (ex: IOException) {
            return HttpResult(-1, "", ex)
        } finally {
            connection?.disconnect()
        }
    }

    private fun endpointOf(source: UpdateSource): String = when (source) {
        UpdateSource.GITEE -> "https://gitee.com/api/v5/repos/ksayus/PaperCoin/releases/latest"
        UpdateSource.GITHUB -> "https://api.github.com/repos/ksayus/PaperCoin/releases/latest"
    }

    private fun sourceName(source: UpdateSource): String =
        if (source == UpdateSource.GITEE) "Gitee" else "GitHub"

    // ---- 解析 ----

    private class ParseFailure(message: String) : Exception(message)

    private fun parseRelease(text: String): UpdateRelease {
        val root = try {
            json.parseToJsonElement(text)
        } catch (ex: Exception) {
            throw ParseFailure("更新源返回的内容看不懂（不是合法 JSON）")
        }

        if (root !is JsonObject) {
            throw ParseFailure("更新源返回的内容看不懂（不是对象）")
        }

        val tag = root.string("tag_name")
        if (tag.isBlank()) {
            throw ParseFailure("更新源没有返回版本号，可能还没有发布过任何版本")
        }

        val version = SemanticVersion.parse(tag)
            ?: throw ParseFailure("更新源的 tag「$tag」不是版本号，无法比较")

        var apkUrl: String? = null
        var apkName: String? = null

        (root["assets"] as? JsonArray)?.forEach { element ->
            val asset = element as? JsonObject ?: return@forEach
            val name = asset.string("name")
            val url = asset.string("browser_download_url")

            if (apkUrl == null && name.endsWith(".apk", ignoreCase = true) && url.isNotBlank()) {
                apkUrl = url
                apkName = name
            }
        }

        return UpdateRelease(
            version = version,
            tagName = tag,
            notes = root.string("body"),
            pageUrl = root.string("html_url"),
            installerUrl = apkUrl,
            installerName = apkName,
        )
    }

    private fun JsonObject.string(key: String): String =
        (this[key] as? JsonPrimitive)?.contentOrNull.orEmpty()

    private fun safeFileName(name: String?): String {
        val base = (name ?: "PaperCoin.apk")
            .substringAfterLast('/')
            .substringAfterLast('\\')

        val cleaned = base.replace(Regex("[^A-Za-z0-9._-]"), "_")

        return if (cleaned.endsWith(".apk", ignoreCase = true)) cleaned else "$cleaned.apk"
    }
}
