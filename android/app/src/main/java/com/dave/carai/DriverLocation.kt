package com.dave.carai

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.location.Geocoder
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.Build
import android.os.Looper
import android.util.Log
import java.util.Locale
import kotlin.math.roundToInt

/** Where the car is right now, so Dave can answer "near me" and "on my route" questions. */
object DriverLocation {

    @Volatile private var latest: Location? = null

    fun hasPermission(context: Context) =
        context.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED ||
            context.checkSelfPermission(Manifest.permission.ACCESS_COARSE_LOCATION) == PackageManager.PERMISSION_GRANTED

    /** Ask for a fresh fix. Called when Dave opens, so it's ready by the time you've finished talking. */
    @Suppress("MissingPermission", "DEPRECATION")
    fun refresh(context: Context) {
        if (!hasPermission(context)) return
        val manager = context.getSystemService(LocationManager::class.java) ?: return
        val providers = listOf(LocationManager.GPS_PROVIDER, LocationManager.NETWORK_PROVIDER)
            .filter { runCatching { manager.isProviderEnabled(it) }.getOrDefault(false) }

        providers.mapNotNull { runCatching { manager.getLastKnownLocation(it) }.getOrNull() }.forEach(::offer)
        for (provider in providers) {
            runCatching {
                if (Build.VERSION.SDK_INT >= 30) {
                    manager.getCurrentLocation(provider, null, context.mainExecutor) { it?.let(::offer) }
                } else {
                    manager.requestSingleUpdate(provider, LocationListener { offer(it) }, Looper.getMainLooper())
                }
            }.onFailure { Log.w("Dave", "Location from $provider failed", it) }
        }
    }

    private fun offer(location: Location) {
        val current = latest
        if (current == null || location.time >= current.time) latest = location
    }

    /** A sentence for the AI about where the car is, or null if unknown. Blocking (address lookup). */
    fun describe(context: Context): String? {
        val location = latest ?: return null
        val minutesOld = ((System.currentTimeMillis() - location.time) / 60_000).toInt()
        if (minutesOld > 60) return null

        val address = runCatching {
            @Suppress("DEPRECATION")
            Geocoder(context, Locale.getDefault()).getFromLocation(location.latitude, location.longitude, 1)
                ?.firstOrNull()?.getAddressLine(0)
        }.getOrNull()

        val coordinates = "%.5f, %.5f".format(Locale.US, location.latitude, location.longitude)
        val where = if (address != null) "$address ($coordinates)" else coordinates
        val age = if (minutesOld >= 2) " (as of $minutesOld minutes ago)" else ""

        val motion = if (location.hasSpeed() && location.speed * 3.6 >= 5) {
            val kmh = (location.speed * 3.6).roundToInt()
            val heading = if (location.hasBearing()) ", heading ${compass(location.bearing)}" else ""
            " The car is driving at about $kmh km/h$heading."
        } else {
            " The car is standing still."
        }
        return "The car is at $where$age.$motion"
    }

    private fun compass(bearing: Float): String {
        val names = listOf("north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest")
        return names[(((bearing % 360) + 360) % 360 / 45f).roundToInt() % 8]
    }
}
