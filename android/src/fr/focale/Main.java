package fr.focale;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.*;
import android.hardware.Camera;
import android.net.Uri;
import android.os.*;
import android.view.*;
import android.widget.*;
import com.google.zxing.*;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeReader;

public class Main extends Activity {
    private LinearLayout page;
    private TextView status;
    private Camera scanner;
    private boolean scanning, autoStart;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final Runnable refresh = new Runnable(){public void run(){
        if(!scanning && status!=null) status.setText(Live.message);
        ui.postDelayed(this,500);
    }};
    private int dp(int x){return Math.round(x*getResources().getDisplayMetrics().density);}
    private TextView label(String text,int size,int color){
        TextView v=new TextView(this); v.setText(text);v.setTextSize(size);v.setTextColor(color);
        v.setPadding(0,0,0,dp(12));page.addView(v);return v;
    }
    private void button(String text,boolean primary,Runnable action){
        Button b=new Button(this);b.setAllCaps(false);b.setText(text);
        b.setBackgroundTintList(android.content.res.ColorStateList.valueOf(primary?0xff252623:0xffe7e8e2));
        b.setTextColor(primary?0xffffffff:0xff252623);b.setOnClickListener(v->action.run());page.addView(b);
    }
    @Override public void onCreate(Bundle state){super.onCreate(state);
        getWindow().setStatusBarColor(0xfff5f5f0);getWindow().setNavigationBarColor(0xfff5f5f0);
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
        home();
        autoStart=state==null && paired() && allowed() && !Live.active;
    }
    private boolean allowed(){return checkSelfPermission(Manifest.permission.CAMERA)==PackageManager.PERMISSION_GRANTED;}
    private boolean paired(){return new Pairing(this).hasLink();}
    private void layout(){
        ScrollView scroll=new ScrollView(this);scroll.setBackgroundColor(0xfff5f5f0);page=new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);page.setPadding(dp(24),dp(32),dp(24),dp(24));
        scroll.addView(page);setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((v,i)->{android.graphics.Insets bars=i.getInsets(WindowInsets.Type.systemBars());
            v.setPadding(bars.left,bars.top,bars.right,bars.bottom);return i;});
    }
    private void home(){
        scanning=false;closeScanner();layout();label("CadreLink",32,0xff242521);
        status=label(Live.message,16,0xff4b5048);
        if(paired()){
            label("PC associé",22,0xff242521);
            Pairing pairing = new Pairing(this);
            label(pairing.prefersUsb() ? "USB préféré" : "Réseau préféré",14,0xff666961);
            if (pairing.hasUsb() && pairing.hasNetwork())
                button(pairing.prefersUsb() ? "Préférer le Wi-Fi" : "Préférer l’USB",false,()->{
                    pairing.preferUsb(!pairing.prefersUsb());
                    stopService(new Intent(this,Live.class));
                    home();
                });
            button("Démarrer la webcam",true,()->permission(false));
            button("Arrêter la diffusion",false,()->{stopService(new Intent(this,Live.class));});
            button("Scanner un QR code de liaison",false,()->permission(true));
        }else{
            label("Associer le téléphone",22,0xff242521);
            label("Ouvre CadreLink sur ton PC, puis scanne le QR code affiché dans le studio.",16,0xff585b54);
            button("Scanner le QR code",true,()->permission(true));
        }
    }
    private boolean pendingScan;
    private void permission(boolean scan){
        pendingScan=scan;
        if(!allowed()){requestPermissions(new String[]{Manifest.permission.CAMERA},10);return;}
        if(scan){if(Live.active)stopService(new Intent(this,Live.class));scan();}
        else startLive();
    }
    @Override public void onRequestPermissionsResult(int id,String[] names,int[] grants){
        super.onRequestPermissionsResult(id,names,grants);
        if(id==10){if(allowed())permission(pendingScan);else Live.message="Autorise la caméra dans les réglages Android pour démarrer.";}
    }
    private void startLive(){
        startForegroundService(new Intent(this,Live.class));
        if(Build.VERSION.SDK_INT>=33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},11);
    }
    private void scan(){
        scanning=true;layout();label("Scanner le QR code",24,0xff242521);
        label("Cadre le code affiché sur le PC.",16,0xff585b54);
        TextureView view=new TextureView(this);page.addView(view,new LinearLayout.LayoutParams(-1,dp(330)));
        button("Retour",false,this::home);
        view.setSurfaceTextureListener(new TextureView.SurfaceTextureListener(){
            public void onSurfaceTextureAvailable(SurfaceTexture surface,int w,int h){
                try{
                    scanner=Camera.open();Camera.Parameters p=scanner.getParameters();
                    if(p.getSupportedFocusModes().contains("continuous-picture"))p.setFocusMode("continuous-picture");
                    scanner.setParameters(p);scanner.setPreviewTexture(surface);scanner.setDisplayOrientation(90);
                    scanner.startPreview();next();
                }catch(Exception e){Live.message="Caméra indisponible. Ferme les applis qui l’utilisent, puis réessaie.";home();}
            }
            public boolean onSurfaceTextureDestroyed(SurfaceTexture s){closeScanner();return true;}
            public void onSurfaceTextureSizeChanged(SurfaceTexture s,int w,int h){}
            public void onSurfaceTextureUpdated(SurfaceTexture s){}
        });
    }
    private void next(){if(scanner!=null)scanner.setOneShotPreviewCallback((bytes,cam)->{
        Camera.Size size=cam.getParameters().getPreviewSize();
        try{
            PlanarYUVLuminanceSource src=new PlanarYUVLuminanceSource(bytes,size.width,size.height,0,0,size.width,size.height,false);
            String value=new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(src))).getText();
            Uri u=Uri.parse(value);
            if(!"focale".equals(u.getScheme()) || !"pair".equals(u.getHost()) ||
                !u.getQueryParameter("pin").matches("[A-F0-9]{64}") || !u.getQueryParameter("key").matches("[A-F0-9]{64}") ||
                !u.getQueryParameter("host").matches("[0-9.]{7,15}"))throw new IllegalArgumentException();
            new Pairing(this).remember(u);
            home();startLive();return;
        }catch(Exception ignored){}
        ui.postDelayed(()->next(),120);
    });}
    private void closeScanner(){if(scanner!=null){scanner.setPreviewCallback(null);scanner.release();scanner=null;}}
    @Override public void onResume(){super.onResume();ui.post(refresh);if(autoStart){autoStart=false;startLive();}}
    @Override public void onPause(){super.onPause();ui.removeCallbacks(refresh);if(scanning)home();}
}