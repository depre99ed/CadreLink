package fr.focale;

import android.content.Context;
import android.content.SharedPreferences;
import android.net.Uri;
import java.util.ArrayList;
import java.util.List;
import java.util.Objects;

final class Pairing {
    private final SharedPreferences prefs;
    Pairing(Context context) { prefs = context.getSharedPreferences("device", 0); }

    boolean hasLink() { return prefs.contains("link") || prefs.contains("usb"); }
    boolean hasUsb() { return prefs.contains("usb"); }
    boolean hasNetwork() { return prefs.contains("link"); }

    void preferUsb(boolean usb) { prefs.edit().putBoolean("preferUsb", usb).apply(); }
    boolean prefersUsb() { return prefs.getBoolean("preferUsb", false); }

    void remember(Uri link) {
        boolean usb = "usb".equals(link.getQueryParameter("transport"));
        String other = usb ? "link" : "usb";
        Uri previous = Uri.parse(prefs.getString(other, ""));
        SharedPreferences.Editor edit = prefs.edit();
        if (previous.getScheme() != null && !Objects.equals(previous.getQueryParameter("pin"), link.getQueryParameter("pin")))
            edit.remove(other);
        edit.putString(usb ? "usb" : "link", link.toString()).putBoolean("preferUsb", usb).apply();
    }

    List<Uri> links() {
        List<Uri> links = new ArrayList<>();
        String[] order = prefersUsb() ? new String[]{"usb", "link"} : new String[]{"link", "usb"};
        for (String key : order) {
            String value = prefs.getString(key, null);
            if (value != null) links.add(Uri.parse(value));
        }
        return links;
    }
}