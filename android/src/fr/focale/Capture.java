package fr.focale;

import android.annotation.SuppressLint;
import android.content.Context;
import android.hardware.camera2.*;
import android.hardware.camera2.params.*;
import android.media.*;
import android.os.*;
import android.util.*;
import android.view.Surface;
import org.json.*;
import java.util.*;
import java.util.concurrent.*;


final class Capture implements AutoCloseable {
    final CameraManager manager;
    final HandlerThread thread=new HandlerThread("Focale caméra");
    final Handler handler;
    final Map<String, Lens> lenses=new LinkedHashMap<>();
    MediaCodec codec;
    CameraDevice device;
    CaptureRequest.Builder request;
    CameraCaptureSession session;
    Surface surface;
    Lens selected;
    volatile String fault;
    int width,height,fps,bitrate;
    boolean natural=true, lockExposure, lockWhiteBalance;
    int exposure;
    private CameraCharacteristics controls;
    private volatile int generation;
    static final class Lens {
        String key, parent, physical, name;
        float min=1,max=1;
        CameraCharacteristics chars;
        JSONObject json() throws JSONException {
            JSONObject o=new JSONObject();o.put("id",key);o.put("name",name);o.put("min",min);o.put("max",max);
            Range<Integer> ev=chars.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE);
            Rational step=chars.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_STEP);
            if(ev!=null && step!=null && physical==null){o.put("evMin",ev.getLower());o.put("evMax",ev.getUpper());o.put("evStep",step.doubleValue());}
            o.put("aeLock",physical==null && Boolean.TRUE.equals(chars.get(CameraCharacteristics.CONTROL_AE_LOCK_AVAILABLE)));
            o.put("awbLock",physical==null && Boolean.TRUE.equals(chars.get(CameraCharacteristics.CONTROL_AWB_LOCK_AVAILABLE)));
            return o;
        }
    }
    Capture(Context c) throws Exception {
        manager=(CameraManager)c.getSystemService(Context.CAMERA_SERVICE);
        for(String id:manager.getCameraIdList()){
            CameraCharacteristics main=manager.getCameraCharacteristics(id);
            add(id,null,main);
            for(String physical:main.getPhysicalCameraIds()){
                try{add(id,physical,manager.getCameraCharacteristics(physical));}catch(Exception ignored){}
            }
        }
        if(lenses.isEmpty())throw new Exception("Aucune caméra vidéo accessible");
        thread.start();handler=new Handler(thread.getLooper());
    }
    private void add(String id,String physical,CameraCharacteristics c) {
        StreamConfigurationMap map=c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        if(map==null || map.getOutputSizes(MediaCodec.class)==null)return;
        Lens l=new Lens();l.parent=id;l.physical=physical;l.key=id+(physical==null?"":"/"+physical);l.chars=c;
        Integer facing=c.get(CameraCharacteristics.LENS_FACING);
        String side=Objects.equals(facing,CameraCharacteristics.LENS_FACING_FRONT)?"Avant":"Arrière";
        float[] lengths=c.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS);
        String focal=lengths!=null && lengths.length>0?String.format(Locale.FRANCE,"%.1f mm",lengths[0]):"Capteur "+(physical==null?id:physical);
        l.name=physical!=null?side+" · "+focal:side+" · "+(c.getPhysicalCameraIds().isEmpty()?focal:"automatique");
        Range<Float> zoom=c.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE);
        if(physical==null && zoom!=null){l.min=zoom.getLower();l.max=zoom.getUpper();}
        lenses.put(l.key,l);
    }
    String primary(){
        for(Lens l:lenses.values())if(l.physical==null && l.name.startsWith("Arrière"))return l.key;
        return lenses.keySet().iterator().next();
    }
    JSONArray inventory() throws JSONException {JSONArray arr=new JSONArray();for(Lens l:lenses.values())arr.put(l.json());return arr;}
    @SuppressLint("MissingPermission")
    void start(String id,float zoom,int requestedFps,int maxWidth) throws Exception {
        clear();fault=null;selected=lenses.get(id);if(selected==null)throw new Exception("Objectif indisponible");
        StreamConfigurationMap map=selected.chars.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        Size best=null;
        for(Size s:map.getOutputSizes(MediaCodec.class))if(s.getWidth()<=maxWidth && s.getWidth()*9==s.getHeight()*16 &&
                (best==null || best.getWidth()<s.getWidth()))best=s;
        if(best==null)throw new Exception("Aucun mode vidéo 16:9 pour cet objectif");
        width=best.getWidth();height=best.getHeight();
        long duration=map.getOutputMinFrameDuration(MediaCodec.class,best);
        int ceiling=duration>0?Math.min(requestedFps,(int)Math.round(1e9/duration)):requestedFps;
        CameraCharacteristics main=manager.getCameraCharacteristics(selected.parent);
        controls=main;
        Range<Integer>[] ranges=main.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
        Range<Integer> rate=null;
        if(ranges!=null)for(Range<Integer> r:ranges)if(r.getUpper()<=ceiling &&
            (rate==null || r.getUpper()>rate.getUpper() || (r.getUpper().equals(rate.getUpper()) && r.getLower()>rate.getLower())))rate=r;
        fps=rate==null?Math.max(1,ceiling):rate.getUpper();bitrate=width>=3840?35000000:width>=1920?(fps>30?18000000:12000000):7000000;
        MediaFormat fmt=MediaFormat.createVideoFormat("video/avc",width,height);
        fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT,MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        fmt.setInteger(MediaFormat.KEY_BIT_RATE,bitrate);fmt.setInteger(MediaFormat.KEY_FRAME_RATE,fps);
        fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL,1);fmt.setInteger(MediaFormat.KEY_MAX_B_FRAMES,0);
        codec=MediaCodec.createEncoderByType("video/avc");codec.configure(fmt,null,null,MediaCodec.CONFIGURE_FLAG_ENCODE);
        surface=codec.createInputSurface();codec.start();
        int current=generation;
        CountDownLatch opened=new CountDownLatch(1);
        manager.openCamera(selected.parent,new CameraDevice.StateCallback(){
            public void onOpened(CameraDevice d){if(current==generation)device=d;else d.close();opened.countDown();}
            public void onDisconnected(CameraDevice d){d.close();if(current==generation)fault="La caméra a été déconnectée";opened.countDown();}
            public void onError(CameraDevice d,int e){d.close();if(current==generation)fault="Caméra indisponible ("+e+")";opened.countDown();}
        },handler);
        if(!opened.await(4,TimeUnit.SECONDS)||device==null||fault!=null)throw new Exception(fault==null?"Ouverture de caméra expirée":fault);
        request=device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);request.addTarget(surface);
        if(rate!=null)request.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,rate);
        int[] modes=main.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES);
        if(modes!=null)for(int m:modes)if(m==CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
            request.set(CaptureRequest.CONTROL_AF_MODE,m);
        OutputConfiguration output=new OutputConfiguration(surface);
        if(selected.physical!=null)output.setPhysicalCameraId(selected.physical);
        CountDownLatch configured=new CountDownLatch(1);
        device.createCaptureSession(new SessionConfiguration(SessionConfiguration.SESSION_REGULAR,Collections.singletonList(output),
            handler::post,new CameraCaptureSession.StateCallback(){
                public void onConfigured(CameraCaptureSession s){if(current==generation)session=s;else s.close();configured.countDown();}
                public void onConfigureFailed(CameraCaptureSession s){s.close();configured.countDown();}
            }));
        if(!configured.await(4,TimeUnit.SECONDS)||session==null)throw new Exception("Mode vidéo refusé par cet objectif");
        applyZoom(zoom);
    }
    void applyZoom(float z) throws Exception{
        if(selected.physical==null && (selected.min!=1||selected.max!=1))
            request.set(CaptureRequest.CONTROL_ZOOM_RATIO,Math.max(selected.min,Math.min(selected.max,z)));
        setMode(CaptureRequest.CONTROL_EFFECT_MODE, CameraCharacteristics.CONTROL_AVAILABLE_EFFECTS, CaptureRequest.CONTROL_EFFECT_MODE_OFF);
        setMode(CaptureRequest.EDGE_MODE, CameraCharacteristics.EDGE_AVAILABLE_EDGE_MODES,
            natural ? CaptureRequest.EDGE_MODE_OFF : CaptureRequest.EDGE_MODE_FAST);
        int[] noise=controls.get(CameraCharacteristics.NOISE_REDUCTION_AVAILABLE_NOISE_REDUCTION_MODES);
        int reduction=natural ? (contains(noise,CaptureRequest.NOISE_REDUCTION_MODE_MINIMAL)
            ? CaptureRequest.NOISE_REDUCTION_MODE_MINIMAL : CaptureRequest.NOISE_REDUCTION_MODE_OFF) : CaptureRequest.NOISE_REDUCTION_MODE_FAST;
        setMode(CaptureRequest.NOISE_REDUCTION_MODE, CameraCharacteristics.NOISE_REDUCTION_AVAILABLE_NOISE_REDUCTION_MODES, reduction);
        Range<Integer> ev=controls.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE);
        if(ev!=null)request.set(CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION,ev.clamp(exposure));
        if(Boolean.TRUE.equals(controls.get(CameraCharacteristics.CONTROL_AE_LOCK_AVAILABLE)))request.set(CaptureRequest.CONTROL_AE_LOCK,lockExposure);
        if(Boolean.TRUE.equals(controls.get(CameraCharacteristics.CONTROL_AWB_LOCK_AVAILABLE)))request.set(CaptureRequest.CONTROL_AWB_LOCK,lockWhiteBalance);
        session.setRepeatingRequest(request.build(),null,handler);
    }
    private static boolean contains(int[] values,int value){
        if(values!=null)for(int candidate:values)if(candidate==value)return true;
        return false;
    }
    private void setMode(CaptureRequest.Key<Integer> key, CameraCharacteristics.Key<int[]> available, int mode){
        if(contains(controls.get(available),mode))request.set(key,mode);
    }
    void keyframe(){Bundle b=new Bundle();b.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME,0);codec.setParameters(b);}
    void adjust(int bits){Bundle b=new Bundle();b.putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE,bits);codec.setParameters(b);}
    void clear(){
        generation++;
        if(session!=null){session.close();session=null;}
        if(device!=null){device.close();device=null;}
        if(codec!=null){try{codec.stop();}catch(Exception ignored){}codec.release();codec=null;}
        if(surface!=null){surface.release();surface=null;}
    }
    public void close(){clear();thread.quitSafely();}
}