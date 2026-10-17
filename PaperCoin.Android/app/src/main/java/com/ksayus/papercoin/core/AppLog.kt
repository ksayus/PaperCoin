package com.ksayus.papercoin.core

/** 轻量日志。单元测试里落到 stdout，应用启动后由 Application 换成 android.util.Log。 */
object AppLog {
    var sink: (String, Throwable?) -> Unit = { message, error ->
        println(message)
        error?.printStackTrace()
    }

    fun warn(message: String, error: Throwable? = null) {
        sink(message, error)
    }
}
