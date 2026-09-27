package com.dave.carai

import android.accessibilityservice.AccessibilityService
import android.content.Intent
import android.view.KeyEvent
import android.view.accessibility.AccessibilityEvent
import android.widget.Toast

/**
 * Sees hardware button presses from every app (including CarPlay) and opens
 * Dave when the chosen steering wheel button is pressed. All other buttons pass through.
 */
class ButtonService : AccessibilityService() {

    override fun onKeyEvent(event: KeyEvent): Boolean {
        val prefs = Prefs(this)

        if (prefs.learning) {
            if (event.action == KeyEvent.ACTION_UP) {
                prefs.keyCode = event.keyCode
                prefs.learning = false
                Toast.makeText(
                    this,
                    "Dave button set to ${KeyEvent.keyCodeToString(event.keyCode)}",
                    Toast.LENGTH_LONG,
                ).show()
            }
            return true
        }

        if (prefs.keyCode != 0 && event.keyCode == prefs.keyCode) {
            if (event.action == KeyEvent.ACTION_UP) {
                // During a music quiz the button means "I know it, stop the snippet"
                val target = if (QuizActivity.active) QuizActivity::class.java else ListenActivity::class.java
                startActivity(Intent(this, target).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
            return true // swallow it so CarPlay/Siri doesn't also react
        }

        return false
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}

    override fun onInterrupt() {}

    /** Runs when the screen boots too, so "Hé Dave" comes back by itself. */
    override fun onServiceConnected() {
        super.onServiceConnected()
        WakeWordService.start(this)
    }
}
