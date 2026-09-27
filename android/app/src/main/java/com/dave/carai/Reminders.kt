package com.dave.carai

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter

/** Timers and reminders: Dave says them out loud when it's time. Survives restarts of the screen. */
object Reminders {

    /** When the reminder goes off: in [minutes] minutes, or at [time] ("HH:mm", today or tomorrow). */
    fun schedule(context: Context, prefs: Prefs, minutes: Int?, time: String?, message: String): Long? {
        val at = when {
            minutes != null && minutes > 0 -> System.currentTimeMillis() + minutes * 60_000L
            !time.isNullOrBlank() -> runCatching {
                val clock = LocalTime.parse(time.trim().padStart(5, '0'))
                var next = ZonedDateTime.now().with(clock).withSecond(0)
                if (next.isBefore(ZonedDateTime.now())) next = next.plusDays(1)
                next.toInstant().toEpochMilli()
            }.getOrNull()
            else -> null
        } ?: return null

        val id = (System.currentTimeMillis() % Int.MAX_VALUE).toInt()
        prefs.reminders = prefs.reminders.put(JSONObject().put("id", id).put("at", at).put("message", message))
        setAlarm(context, id, at, message)
        return at
    }

    fun cancelAll(context: Context, prefs: Prefs): Int {
        val all = prefs.reminders
        for (i in 0 until all.length()) {
            all.optJSONObject(i)?.let { alarmManager(context).cancel(pendingIntent(context, it.optInt("id"), "")) }
        }
        prefs.reminders = JSONArray()
        return all.length()
    }

    /** For the AI: what's coming up, e.g. "10:30 get fuel". */
    fun describe(prefs: Prefs): String? {
        val all = prefs.reminders
        if (all.length() == 0) return null
        return (0 until all.length()).mapNotNull { all.optJSONObject(it) }
            .sortedBy { it.optLong("at") }
            .joinToString("; ") { "${clock(it.optLong("at"))} ${it.optString("message")}" }
    }

    fun clock(epochMs: Long): String =
        Instant.ofEpochMilli(epochMs).atZone(ZoneId.systemDefault()).format(DateTimeFormatter.ofPattern("HH:mm"))

    /** After the screen restarts, alarms are gone; set them again (overdue ones go off in a minute). */
    fun restore(context: Context, prefs: Prefs) {
        val all = prefs.reminders
        val kept = JSONArray()
        for (i in 0 until all.length()) {
            val reminder = all.optJSONObject(i) ?: continue
            val at = reminder.optLong("at")
            if (at < System.currentTimeMillis() - 60 * 60_000L) continue // more than an hour late: drop it
            kept.put(reminder)
            setAlarm(context, reminder.optInt("id"), maxOf(at, System.currentTimeMillis() + 60_000L), reminder.optString("message"))
        }
        prefs.reminders = kept
    }

    fun remove(prefs: Prefs, id: Int) {
        val all = prefs.reminders
        val kept = JSONArray()
        for (i in 0 until all.length()) all.optJSONObject(i)?.takeIf { it.optInt("id") != id }?.let { kept.put(it) }
        prefs.reminders = kept
    }

    private fun setAlarm(context: Context, id: Int, at: Long, message: String) {
        val manager = alarmManager(context)
        val intent = pendingIntent(context, id, message)
        val exact = Build.VERSION.SDK_INT < 31 || manager.canScheduleExactAlarms()
        if (exact) manager.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, intent)
        else manager.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, intent)
        Log.d("Dave", "Reminder $id at ${clock(at)} (exact=$exact): $message")
    }

    private fun alarmManager(context: Context) = context.getSystemService(AlarmManager::class.java)

    private fun pendingIntent(context: Context, id: Int, message: String) = PendingIntent.getBroadcast(
        context, id,
        Intent(context, ReminderReceiver::class.java).putExtra("id", id).putExtra("message", message),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )
}

/** Goes off when a reminder is due (and after the screen boots, to set alarms again). */
class ReminderReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val prefs = Prefs(context)
        if (intent.action == Intent.ACTION_BOOT_COMPLETED) {
            Reminders.restore(context, prefs)
            WakeWordService.start(context)
            return
        }
        val message = intent.getStringExtra("message") ?: return
        Reminders.remove(prefs, intent.getIntExtra("id", 0))
        context.startActivity(Intent(context, ListenActivity::class.java)
            .putExtra(ListenActivity.EXTRA_ANNOUNCE, message)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}
