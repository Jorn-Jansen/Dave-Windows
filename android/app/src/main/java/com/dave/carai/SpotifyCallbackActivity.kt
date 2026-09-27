package com.dave.carai

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.util.Log
import android.widget.Toast
import kotlin.concurrent.thread

/** Spotify sends the browser back here after you log in. */
class SpotifyCallbackActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val prefs = Prefs(this)
        val code = intent.data?.getQueryParameter("code")
        val error = intent.data?.getQueryParameter("error")

        if (code == null) {
            done("Spotify login cancelled${error?.let { " ($it)" } ?: ""}")
            return
        }
        thread {
            val message = try {
                SpotifyClient.finishLogin(prefs, code)
                "Spotify connected ✅"
            } catch (e: Exception) {
                Log.w("Dave", "Spotify login failed", e)
                "Spotify login failed: ${e.message}"
            }
            runOnUiThread { done(message) }
        }
    }

    private fun done(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show()
        startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP))
        finish()
    }
}
