package fr.focale;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.media.*;
import android.net.Uri;
import android.os.*;
import org.json.*;
import java.io.*;
import java.net.*;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.cert.*;
import java.util.*;
import java.util.concurrent.*;
import javax.net.ssl.*;

public class Live extends Service {
    static volatile String message="Prêt à connecter";
    static volatile boolean active;
    private volatile boolean running;
    private volatile Socket connection;
    private PowerManager.WakeLock wake;
    private Thread worker;
    @Override public IBinder onBind(Intent i){return null;}
    private Notification notice(String text){
        PendingIntent open=PendingIntent.getActivity(this,1,new Intent(this,Main.class),PendingIntent.FLAG_IMMUTABLE);
        PendingIntent stop=PendingIntent.getService(this,2,new Intent(this,Live.class).setAction("stop"),PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this,"video").setSmallIcon(fr.focale.cam.R.drawable.cadrelink_notification).setContentTitle("CadreLink")
            .setContentText(text).setContentIntent(open).setOngoing(true).addAction(0,"Arrêter",stop).build();
    }
    private void state(String s){if(!running)return;message=s;getSystemService(NotificationManager.class).notify(25,notice(s));}
    @Override public int onStartCommand(Intent intent,int flags,int startId){
        if(intent==null || "stop".equals(intent.getAction())){stopSelf();return START_NOT_STICKY;}
        if(running)return START_NOT_STICKY;
        getSystemService(NotificationManager.class).createNotificationChannel(new NotificationChannel("video","Webcam en direct",NotificationManager.IMPORTANCE_LOW));
        startForeground(25,notice("Connexion au studio…"),ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA);
        wake=((PowerManager)getSystemService(POWER_SERVICE)).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"CadreLink:Video");
        wake.acquire();active=running=true;worker=new Thread(this::run,"CadreLink vidéo");worker.start();
        return START_NOT_STICKY;
    }
    @Override public void onDestroy(){
        running=false;active=false;message="Diffusion arrêtée";closeSocket();if(worker!=null)worker.interrupt();
        if(wake!=null&&wake.isHeld())wake.release();stopForeground(STOP_FOREGROUND_REMOVE);super.onDestroy();
    }
    private void closeSocket(){Socket s=connection;if(s!=null)try{s.close();}catch(IOException ignored){}}
    private void run(){
        Pairing pairing = new Pairing(this);
        int backoff = 600;
        while(running){
            for (Uri link : pairing.links()) {
                if (!running) break;
                try {
                    state("Connexion au studio…");
                    long started = SystemClock.elapsedRealtime();
                    try { connect(link); }
                    finally { if (SystemClock.elapsedRealtime()-started > 12000) backoff = 600; }
                    if (running) break;
                } catch (Exception e) {
                    state("Reconnexion · " + (e.getMessage() == null ? "vérifie le réseau local" : e.getMessage()));
                } finally {
                    closeSocket();
                }
            }
            try{Thread.sleep(backoff);backoff=Math.min(8000,backoff*2);}catch(InterruptedException ignored){break;}
        }
    }
    private static String hex(byte[] data){StringBuilder b=new StringBuilder();for(byte x:data)b.append(String.format(Locale.US,"%02X",x));return b.toString();}
    private void connect(Uri link) throws Exception {
        String host=link.getQueryParameter("host"),pin=link.getQueryParameter("pin");
        boolean usb = "usb".equals(link.getQueryParameter("transport"));
        // In cable mode Android and Windows are on the network created by USB tethering.
        // Do not attempt a previously stored private USB address over cellular.
        if (usb && !hasLocalRoute(host)) throw new IOException("Active Point d’accès USB puis réessaie");
        SSLContext ctx=SSLContext.getInstance("TLS");
        ctx.init(null,new TrustManager[]{new X509TrustManager(){
            public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}
            public void checkClientTrusted(X509Certificate[] c,String t)throws CertificateException{throw new CertificateException();}
            public void checkServerTrusted(X509Certificate[] chain,String t)throws CertificateException{
                try{if(chain==null||chain.length==0||!pin.equals(hex(MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded()))))
                    throw new CertificateException("Scanne le QR code du PC à nouveau");}
                catch(NoSuchAlgorithmException e){throw new CertificateException(e);}
            }
        }},null);
        try(Socket raw=new Socket()){
            connection=raw;raw.setTcpNoDelay(true);raw.setSendBufferSize(128*1024);raw.connect(new InetSocketAddress(host,47472),2300);
            try(SSLSocket secure=(SSLSocket)ctx.getSocketFactory().createSocket(raw,host,47472,true)){
                secure.setSoTimeout(8000);secure.startHandshake();
                DataOutputStream out=new DataOutputStream(new BufferedOutputStream(secure.getOutputStream()));
                BufferedReader in=new BufferedReader(new InputStreamReader(secure.getInputStream(),StandardCharsets.UTF_8));
                json(out,new JSONObject().put("type","hello").put("key",link.getQueryParameter("key")).put("model",Build.MODEL).put("transport",usb?"usb":"network"));
                if(!"ready".equals(in.readLine()))throw new IOException("Association refusée par le PC");
                secure.setSoTimeout(15000);
                try(Capture camera=new Capture(this)){
                    LinkedBlockingQueue<JSONObject> commands=new LinkedBlockingQueue<>(64);
                    Flow flow=new Flow();
                    Thread reader=new Thread(()->{
                        try{String line;while((line=in.readLine())!=null){
                            JSONObject m=new JSONObject(line);
                            if(m.has("ack")) flow.ack=m.getInt("ack");
                            if ((!m.has("ack") || m.has("ping")) && !commands.offer(m))
                                throw new IOException("Trop de commandes");
                        }}catch(Exception ignored){} finally{try{secure.close();}catch(IOException ignored){}}
                    },"CadreLink commandes");reader.setDaemon(true);reader.start();
                    json(out,new JSONObject().put("type","lenses").put("items",camera.inventory()));
                    stream(camera,out,commands,flow,secure);
                }
            }
        }
    }
    static boolean hasLocalRoute(String host) {
        try {
            InetAddress target = InetAddress.getByName(host);
            for (NetworkInterface adapter : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!adapter.isUp() || adapter.isLoopback()) continue;
                for (InterfaceAddress address : adapter.getInterfaceAddresses()) {
                    if (!(address.getAddress() instanceof Inet4Address)) continue;
                    short prefix = address.getNetworkPrefixLength();
                    if (prefix <= 0 || prefix > 32) continue;
                    int mask = -1 << (32 - prefix);
                    if ((ByteBuffer.wrap(target.getAddress()).getInt() & mask) ==
                        (ByteBuffer.wrap(address.getAddress().getAddress()).getInt() & mask)) return true;
                }
            }
        } catch (Exception ignored) {}
        return false;
    }
    static class Flow { volatile int ack; int sent; }
    static void packet(DataOutputStream out,int type,byte[] payload)throws IOException{
        out.writeByte(type);out.writeInt(payload.length);out.write(payload);out.flush();
    }
    static void json(DataOutputStream out,JSONObject o)throws IOException{packet(out,1,o.toString().getBytes(StandardCharsets.UTF_8));}
    private void stream(Capture c,DataOutputStream out,BlockingQueue<JSONObject> commands,Flow flow,Socket socket)throws Exception{
        String lens=c.primary();float zoom=1;int target=60,limit=1920;boolean restart=true,waitingKey=true;
        MediaCodec.BufferInfo info=new MediaCodec.BufferInfo();byte[] config=new byte[0];
        long adjustment=0,lastStall=0,nextBattery=0;int currentRate=0;
        while(running&&!socket.isClosed()){
            long time = SystemClock.elapsedRealtime();
            if (time >= nextBattery) {
                BatteryManager battery = getSystemService(BatteryManager.class);
                int percent = battery.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY);
                if (percent >= 0 && percent <= 100)
                    json(out,new JSONObject().put("type","battery").put("percent",percent).put("charging",battery.isCharging()));
                nextBattery = time + 15000;
            }
            JSONObject m;
            while((m=commands.poll())!=null){
                if (m.has("ping")) {
                    json(out,new JSONObject().put("type","pong").put("time",m.getLong("ping")));
                    continue;
                }
                if(m.has("lens") && c.lenses.containsKey(m.optString("lens"))){
                    String id=m.getString("lens");if(!id.equals(lens)){restart=true;zoom=1;c.exposure=0;c.lockExposure=false;c.lockWhiteBalance=false;}lens=id;
                }
                if(m.has("width")){int w=m.optInt("width")==3840?3840:1920;restart|=w!=limit;limit=w;}
                if(m.has("fps")){int f=m.optInt("fps")==30?30:60;restart|=f!=target;target=f;}
                boolean update=m.has("zoom") || m.has("natural") || m.has("exposure") || m.has("aeLock") || m.has("awbLock");
                c.natural=m.optBoolean("natural",c.natural);
                c.exposure=m.optInt("exposure",c.exposure);
                c.lockExposure=m.optBoolean("aeLock",c.lockExposure);
                c.lockWhiteBalance=m.optBoolean("awbLock",c.lockWhiteBalance);
                if(m.has("zoom")){
                    float z=(float)m.getDouble("zoom");if(Float.isFinite(z))zoom=z;
                }
                if(update&&!restart)c.applyZoom(zoom);
                if(m.optBoolean("report")&&!restart)
                    json(out,new JSONObject().put("type","settings").put("lens",lens).put("zoom",zoom));
            }
            if(restart){
                try{c.start(lens,zoom,target,limit);}catch(Exception first){
                    try{try{c.start(lens,zoom,30,1920);}catch(Exception unsupported){c.start(lens,zoom,30,1280);}}catch(Exception second){
                        if(lens.equals(c.primary()))throw second;
                        json(out,new JSONObject().put("type","warning").put("text","Objectif refusé par Android. Retour à la caméra principale."));
                        lens=c.primary();zoom=1;c.start(lens,zoom,30,1920);
                    }
                }
                currentRate=c.bitrate;config=new byte[0];waitingKey=true;restart=false;
                json(out,new JSONObject().put("type","format").put("width",c.width).put("height",c.height).put("fps",c.fps).put("lens",lens).put("zoom",zoom));
                state("Diffusion active · "+c.width+" × "+c.height+" · "+c.fps+" i/s");
            }
            if(c.fault!=null)throw new IOException(c.fault);
            int idx=c.codec.dequeueOutputBuffer(info,15000);
            if(idx==MediaCodec.INFO_OUTPUT_FORMAT_CHANGED){
                MediaFormat fmt=c.codec.getOutputFormat();ByteArrayOutputStream cs=new ByteArrayOutputStream();
                for(String key:new String[]{"csd-0","csd-1"})if(fmt.containsKey(key)){
                    ByteBuffer b=fmt.getByteBuffer(key).duplicate();byte[] bytes=new byte[b.remaining()];b.get(bytes);cs.write(bytes);
                }
                config=cs.toByteArray();continue;
            }
            if(idx<0)continue;
            byte[] au=null;boolean key=(info.flags&MediaCodec.BUFFER_FLAG_KEY_FRAME)!=0;
            try{
                if(info.size>0 && (info.flags&MediaCodec.BUFFER_FLAG_CODEC_CONFIG)==0){
                    long now=SystemClock.elapsedRealtime();
                    if(flow.sent-flow.ack>=Math.max(4,c.fps/8)){
                        lastStall=now;waitingKey=true;
                        if(now-adjustment>800){currentRate=Math.max(2500000,currentRate*3/4);c.adjust(currentRate);adjustment=now;}
                    }else if(!waitingKey||key){
                        waitingKey=false;int prefix=key?config.length:0;
                        au=new byte[prefix+info.size];if(key)System.arraycopy(config,0,au,0,prefix);
                        ByteBuffer b=c.codec.getOutputBuffer(idx);b.position(info.offset);b.limit(info.offset+info.size);b.get(au,prefix,info.size);
                        if(currentRate<c.bitrate&&now-Math.max(adjustment,lastStall)>4000){currentRate=Math.min(c.bitrate,currentRate+1000000);c.adjust(currentRate);adjustment=now;}
                    }else c.keyframe();
                }
            }finally{c.codec.releaseOutputBuffer(idx,false);}
            if(au!=null){flow.sent++;packet(out,2,au);}
        }
    }
}