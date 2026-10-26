// File: BleBridge.java
// Polar Verity Sense PPI acquisition for the Quest 3 (Android 12L, API 32).
//
// Native responsibilities only: scan, connect, service discovery, MTU, notification set-up on
// BOTH PMD characteristics, control-point command/response, data-frame framing, and the
// extraction of EVERY raw PPI sample. No statistics, no interpretation.
//
// Protocol (official Polar BLE SDK, see HRV_RECONSTRUCTION_AUDIT.md section D):
//   PMD service        fb005c80-02e7-f387-1cad-8acd2d8df0c8
//   control point      fb005c81-...  (write command; response/notification F0 <op> <type> <status> <more> ...)
//   data               fb005c82-...  (notifications: [type][u64 LE timestamp][frameType][6-byte samples...])
//   PPI type 0x03; start = 02 03; stop = 03 03
//   sample = hr u8 | ppi u16 LE | errorEstimate u16 LE | flags (bit0 blocker, bit1 skin contact, bit2 skin contact supported)
//
// Every state change goes to Unity as   OnBleState("<state>|<detail>")
// every data notification as            OnBlePacket("P|<seq>|<len>|<type>|<frameType>|<n>")
// every sample as                       OnPpi("S|<seq>|<idx>|<n>|<len>|<tsNs>|<hr>|<ppi>|<err>|<flags>")
// The Bluetooth address is never sent to Unity.

package com.adaptivear.ble;

import android.Manifest;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import java.util.List;
import java.util.UUID;

public class BleBridge extends BluetoothGattCallback {
    public static final String VERSION = "1.0.0";
    private static final String TAG = "AdaptiveAR-BLE";

    private static final UUID PMD_SERVICE = UUID.fromString("fb005c80-02e7-f387-1cad-8acd2d8df0c8");
    private static final UUID PMD_CP      = UUID.fromString("fb005c81-02e7-f387-1cad-8acd2d8df0c8");
    private static final UUID PMD_DATA    = UUID.fromString("fb005c82-02e7-f387-1cad-8acd2d8df0c8");
    private static final UUID CCCD        = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");

    private static final int PMD_TYPE_PPI = 0x03;
    private static final int OP_START = 0x02;
    private static final int OP_STOP = 0x03;
    private static final int RESPONSE_CODE = 0xF0;
    private static final int STATUS_SUCCESS = 0;
    private static final int STATUS_ALREADY_IN_STATE = 6;
    private static final int FRAME_HEADER = 10;
    private static final int PPI_SAMPLE_BYTES = 6;

    private Context context;
    private String target = "PolarPpiReceiver";
    private String nameFilter = "Polar";
    private int requestedMtu = 512;
    private int maxReconnects = 20;
    private int scanTimeoutMs = 30000;

    private final Handler main = new Handler(Looper.getMainLooper());
    private BluetoothAdapter adapter;
    private BluetoothLeScanner scanner;
    private ScanCallback scanCallback;
    private BluetoothGatt gatt;
    private BluetoothGattCharacteristic cpChar;
    private BluetoothGattCharacteristic dataChar;

    private boolean scanning;
    private boolean stopRequested;
    private boolean streamStarted;
    private boolean firstDataSeen;
    private boolean restartAfterStop;
    private int reconnectAttempts;
    private int grantedMtu = 23;
    private long packetSeq;
    private String deviceName = "";
    private String state = "idle";

    // ------------------------------------------------------------------ API called from Unity

    public void init(Context ctx, String targetGameObject, String deviceNameFilter, int mtu, int maxReconnectAttempts) {
        this.context = ctx;
        if (targetGameObject != null && targetGameObject.length() > 0) this.target = targetGameObject;
        if (deviceNameFilter != null && deviceNameFilter.length() > 0) this.nameFilter = deviceNameFilter;
        if (mtu > 23) this.requestedMtu = mtu;
        this.maxReconnects = maxReconnectAttempts;
        BluetoothManager bm = (BluetoothManager) ctx.getSystemService(Context.BLUETOOTH_SERVICE);
        adapter = bm != null ? bm.getAdapter() : null;
        if (adapter == null) { setState("error", "no_bluetooth_adapter"); return; }
        setState("initialised", "version=" + VERSION + " adapterEnabled=" + adapter.isEnabled() + " filter=" + nameFilter + " mtu=" + requestedMtu);
    }

    public String getState() { return state; }
    public int getGrantedMtu() { return grantedMtu; }
    public boolean isStreaming() { return streamStarted; }

    public void startScan() {
        if (adapter == null) { setState("error", "not_initialised"); return; }
        if (!hasPermission(Manifest.permission.BLUETOOTH_SCAN) || !hasPermission(Manifest.permission.BLUETOOTH_CONNECT)) {
            setState("permission_missing", "scan=" + hasPermission(Manifest.permission.BLUETOOTH_SCAN) + " connect=" + hasPermission(Manifest.permission.BLUETOOTH_CONNECT));
            return;
        }
        if (!adapter.isEnabled()) { setState("error", "bluetooth_off"); return; }
        if (scanning) { setState("scanning", "already"); return; }
        stopRequested = false;
        scanner = adapter.getBluetoothLeScanner();
        if (scanner == null) { setState("error", "no_le_scanner"); return; }

        scanCallback = new ScanCallback() {
            @Override public void onScanResult(int callbackType, ScanResult result) {
                try {
                    BluetoothDevice device = result.getDevice();
                    String name = device.getName();
                    if (name == null) name = result.getScanRecord() != null ? result.getScanRecord().getDeviceName() : null;
                    if (name == null || !name.contains(nameFilter)) return;
                    if (!scanning) return;
                    scanning = false;
                    try { scanner.stopScan(this); } catch (Exception ignored) {}
                    deviceName = name;
                    setState("found", "name=" + name + " rssi=" + result.getRssi());
                    connect(device);
                } catch (SecurityException se) {
                    setState("permission_missing", "scan_result " + se.getMessage());
                }
            }
            @Override public void onScanFailed(int errorCode) {
                scanning = false;
                setState("error", "scan_failed code=" + errorCode);
                scheduleReconnect();
            }
        };
        try {
            ScanSettings settings = new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build();
            scanner.startScan(null, settings, scanCallback);
            scanning = true;
            setState("scanning", "filter=" + nameFilter + " attempt=" + reconnectAttempts);
            main.postDelayed(new Runnable() { @Override public void run() {
                if (scanning) {
                    scanning = false;
                    try { scanner.stopScan(scanCallback); } catch (Exception ignored) {}
                    setState("scan_timeout", "after_ms=" + scanTimeoutMs);
                    scheduleReconnect();
                }
            }}, scanTimeoutMs);
        } catch (SecurityException se) {
            setState("permission_missing", "startScan " + se.getMessage());
        }
    }

    /** Stops the PPI stream, disconnects and closes. No reconnect afterwards. */
    public void stop() {
        stopRequested = true;
        if (scanning && scanner != null && scanCallback != null) {
            scanning = false;
            try { scanner.stopScan(scanCallback); } catch (Exception ignored) {}
        }
        if (gatt != null && cpChar != null && streamStarted) {
            writeCp(new byte[] { (byte) OP_STOP, (byte) PMD_TYPE_PPI }, "03 03");
            main.postDelayed(new Runnable() { @Override public void run() { closeGatt("stopped"); } }, 400);
        } else {
            closeGatt("stopped");
        }
    }

    // ------------------------------------------------------------------ connection

    private void connect(BluetoothDevice device) {
        try {
            setState("connecting", "name=" + deviceName);
            gatt = device.connectGatt(context, false, this, BluetoothDevice.TRANSPORT_LE);
        } catch (SecurityException se) {
            setState("permission_missing", "connectGatt " + se.getMessage());
        }
    }

    @Override public void onConnectionStateChange(BluetoothGatt g, int status, int newState) {
        if (newState == BluetoothProfile.STATE_CONNECTED && status == BluetoothGatt.GATT_SUCCESS) {
            reconnectAttempts = 0;
            setState("connected", "status=" + status);
            try { g.discoverServices(); } catch (SecurityException se) { setState("permission_missing", "discoverServices"); }
            return;
        }
        streamStarted = false;
        setState("disconnected", "status=" + status + " newState=" + newState);
        closeGatt(null);
        scheduleReconnect();
    }

    @Override public void onServicesDiscovered(BluetoothGatt g, int status) {
        if (status != BluetoothGatt.GATT_SUCCESS) { setState("error", "service_discovery status=" + status); return; }
        StringBuilder sb = new StringBuilder();
        List<BluetoothGattService> services = g.getServices();
        for (BluetoothGattService s : services) { if (sb.length() > 0) sb.append(','); sb.append(s.getUuid().toString()); }
        setState("services_discovered", "count=" + services.size() + " uuids=" + sb);

        BluetoothGattService pmd = g.getService(PMD_SERVICE);
        if (pmd == null) { setState("error", "pmd_service_missing"); return; }
        cpChar = pmd.getCharacteristic(PMD_CP);
        dataChar = pmd.getCharacteristic(PMD_DATA);
        if (cpChar == null || dataChar == null) { setState("error", "pmd_characteristics_missing cp=" + (cpChar != null) + " data=" + (dataChar != null)); return; }

        try {
            boolean ok = g.requestMtu(requestedMtu);
            setState("mtu_requested", "requested=" + requestedMtu + " accepted=" + ok);
            if (!ok) enableControlPoint();
        } catch (SecurityException se) { setState("permission_missing", "requestMtu"); }
    }

    @Override public void onMtuChanged(BluetoothGatt g, int mtu, int status) {
        grantedMtu = mtu;
        setState("mtu", "requested=" + requestedMtu + " granted=" + mtu + " status=" + status + " maxSamplesPerNotification=" + Math.max(0, (mtu - 3 - FRAME_HEADER) / PPI_SAMPLE_BYTES));
        enableControlPoint();
    }

    private void enableControlPoint() { enableNotifications(cpChar, "cp"); }

    private void enableNotifications(BluetoothGattCharacteristic ch, String label) {
        try {
            boolean local = gatt.setCharacteristicNotification(ch, true);
            BluetoothGattDescriptor d = ch.getDescriptor(CCCD);
            if (d == null) { setState("error", label + "_cccd_missing"); return; }
            boolean indicate = (ch.getProperties() & BluetoothGattCharacteristic.PROPERTY_INDICATE) != 0
                               && (ch.getProperties() & BluetoothGattCharacteristic.PROPERTY_NOTIFY) == 0;
            d.setValue(indicate ? BluetoothGattDescriptor.ENABLE_INDICATION_VALUE : BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
            boolean queued = gatt.writeDescriptor(d);
            setState(label + "_notify_requested", "local=" + local + " mode=" + (indicate ? "indicate" : "notify") + " queued=" + queued + " props=" + ch.getProperties());
        } catch (SecurityException se) { setState("permission_missing", "enableNotifications " + label); }
    }

    @Override public void onDescriptorWrite(BluetoothGatt g, BluetoothGattDescriptor d, int status) {
        UUID owner = d.getCharacteristic().getUuid();
        if (PMD_CP.equals(owner)) {
            setState("cp_notifications", "status=" + status);
            if (status == BluetoothGatt.GATT_SUCCESS) enableNotifications(dataChar, "data");
            else setState("error", "cp_cccd_write status=" + status);
        } else if (PMD_DATA.equals(owner)) {
            setState("data_notifications", "status=" + status);
            if (status == BluetoothGatt.GATT_SUCCESS) {
                setState("notifications_enabled", "cp+data");
                requestStart();
            } else setState("error", "data_cccd_write status=" + status);
        }
    }

    private void requestStart() {
        packetSeq = 0;
        firstDataSeen = false;
        writeCp(new byte[] { (byte) OP_START, (byte) PMD_TYPE_PPI }, "02 03");
        setState("stream_requested", "command=02 03");
    }

    private void writeCp(byte[] value, String label) {
        try {
            cpChar.setValue(value);
            cpChar.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT);
            boolean queued = gatt.writeCharacteristic(cpChar);
            if (!queued) setState("error", "cp_write_not_queued " + label);
        } catch (SecurityException se) { setState("permission_missing", "writeCharacteristic"); }
    }

    @Override public void onCharacteristicWrite(BluetoothGatt g, BluetoothGattCharacteristic ch, int status) {
        if (PMD_CP.equals(ch.getUuid()))
            setState("cp_write", "status=" + status + " bytes=" + hex(ch.getValue(), 8) + " (write acknowledged; stream not started until F0 response)");
    }

    // ------------------------------------------------------------------ notifications

    @Override public void onCharacteristicChanged(BluetoothGatt g, BluetoothGattCharacteristic ch) {
        byte[] data = ch.getValue();
        if (data == null) return;
        if (PMD_CP.equals(ch.getUuid())) { handleControlPoint(data); return; }
        if (PMD_DATA.equals(ch.getUuid())) { handleData(data); }
    }

    private void handleControlPoint(byte[] data) {
        String bytes = hex(data, 16);
        if (data.length >= 4 && (data[0] & 0xFF) == RESPONSE_CODE) {
            int op = data[1] & 0xFF, type = data[2] & 0xFF, status = data[3] & 0xFF;
            boolean more = data.length > 4 && data[4] != 0;
            if (op == OP_START && type == PMD_TYPE_PPI) {
                if (status == STATUS_SUCCESS) {
                    streamStarted = true;
                    setState("stream_started", "response=" + bytes + " more=" + more);
                } else if (status == STATUS_ALREADY_IN_STATE) {
                    restartAfterStop = true;
                    setState("stream_already_in_state", "response=" + bytes + " -> sending 03 03 then 02 03");
                    writeCp(new byte[] { (byte) OP_STOP, (byte) PMD_TYPE_PPI }, "03 03");
                } else {
                    setState("stream_error", "response=" + bytes + " status=" + status);
                }
            } else if (op == OP_STOP && type == PMD_TYPE_PPI) {
                streamStarted = false;
                setState("stream_stop_ack", "response=" + bytes);
                if (restartAfterStop && !stopRequested) { restartAfterStop = false; requestStart(); }
            } else {
                setState("cp_response", "response=" + bytes);
            }
            return;
        }
        if (data.length >= 1 && (data[0] & 0xFF) == 0x01) {        // service -> client: online measurement stopped
            streamStarted = false;
            setState("stream_stopped", "by_device bytes=" + bytes);
            return;
        }
        setState("cp_unknown", "bytes=" + bytes);
    }

    private void handleData(byte[] data) {
        packetSeq++;
        int len = data.length;
        if (len < FRAME_HEADER) { setState("malformed_packet", "seq=" + packetSeq + " len=" + len + " reason=short bytes=" + hex(data, 32)); return; }
        int type = data[0] & 0xFF;
        int frameTypeField = data[9] & 0xFF;
        int frameType = frameTypeField & 0x7F;
        boolean compressed = (frameTypeField & 0x80) != 0;
        if (type != PMD_TYPE_PPI) { setState("other_measurement_type", "seq=" + packetSeq + " type=" + type + " len=" + len); return; }
        if (compressed || frameType != 0) { setState("malformed_packet", "seq=" + packetSeq + " len=" + len + " reason=frameType=" + frameType + " compressed=" + compressed + " bytes=" + hex(data, 32)); return; }
        int payload = len - FRAME_HEADER;
        if (payload <= 0 || payload % PPI_SAMPLE_BYTES != 0) { setState("malformed_packet", "seq=" + packetSeq + " len=" + len + " reason=payload_not_multiple_of_6 bytes=" + hex(data, 32)); return; }
        int n = payload / PPI_SAMPLE_BYTES;

        long ts = 0;
        for (int i = 0; i < 8; i++) ts |= ((long) (data[1 + i] & 0xFF)) << (8 * i);
        String tsText = Long.toUnsignedString(ts);

        if (!firstDataSeen) { firstDataSeen = true; setState("data_first", "seq=" + packetSeq + " len=" + len + " samples=" + n + " bytes=" + hex(data, 32)); }
        send("OnBlePacket", "P|" + packetSeq + "|" + len + "|" + type + "|" + frameType + "|" + n);

        for (int i = 0; i < n; i++) {
            int o = FRAME_HEADER + i * PPI_SAMPLE_BYTES;
            int hr = data[o] & 0xFF;
            int ppi = (data[o + 1] & 0xFF) | ((data[o + 2] & 0xFF) << 8);
            int err = (data[o + 3] & 0xFF) | ((data[o + 4] & 0xFF) << 8);
            int flags = data[o + 5] & 0xFF;
            send("OnPpi", "S|" + packetSeq + "|" + i + "|" + n + "|" + len + "|" + tsText + "|" + hr + "|" + ppi + "|" + err + "|" + flags);
        }
    }

    // ------------------------------------------------------------------ housekeeping

    private void scheduleReconnect() {
        if (stopRequested) return;
        reconnectAttempts++;
        if (reconnectAttempts > maxReconnects) { setState("gave_up", "attempts=" + reconnectAttempts); return; }
        long delay = 2000L * Math.min(reconnectAttempts, 5);
        setState("reconnect_scheduled", "attempt=" + reconnectAttempts + " in_ms=" + delay);
        main.postDelayed(new Runnable() { @Override public void run() { if (!stopRequested) startScan(); } }, delay);
    }

    private void closeGatt(String stateAfter) {
        streamStarted = false;
        if (gatt != null) {
            try { gatt.disconnect(); } catch (Exception ignored) {}
            try { gatt.close(); } catch (Exception ignored) {}
            gatt = null;
        }
        cpChar = null; dataChar = null;
        if (stateAfter != null) setState(stateAfter, "closed");
    }

    private boolean hasPermission(String permission) {
        return context != null && context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED;
    }

    private void setState(String s, String detail) {
        state = s;
        Log.i(TAG, s + " | " + detail);
        send("OnBleState", s + "|" + (detail == null ? "" : detail) + "|" + deviceName);
    }

    private void send(String method, String message) {
        try { com.unity3d.player.UnityPlayer.UnitySendMessage(target, method, message); }
        catch (Throwable t) { Log.w(TAG, "UnitySendMessage failed: " + t.getMessage()); }
    }

    private static String hex(byte[] b, int max) {
        if (b == null) return "";
        StringBuilder sb = new StringBuilder();
        int n = Math.min(b.length, max);
        for (int i = 0; i < n; i++) { if (i > 0) sb.append(' '); sb.append(String.format("%02X", b[i] & 0xFF)); }
        if (b.length > max) sb.append(" ...");
        return sb.toString();
    }
}
