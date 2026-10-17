package com.ksayus.papercoin

import android.app.Application
import android.util.Log
import com.ksayus.papercoin.core.AppLog
import com.ksayus.papercoin.data.AppContainer

class PaperCoinApplication : Application() {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()

        AppLog.sink = { message, error ->
            if (error != null) Log.w("PaperCoin", message, error) else Log.i("PaperCoin", message)
        }

        container = AppContainer(this)
    }
}
