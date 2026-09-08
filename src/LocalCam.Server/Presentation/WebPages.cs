namespace LocalCam.Server.Presentation;

public static class WebPages
{
    public const string Bootstrap = """
        <!doctype html><html lang="zh-CN"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>LocalCam - 安装证书</title><style>body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;margin:2rem;line-height:1.55;color:#172033}a,button{font-size:1rem}code{overflow-wrap:anywhere}</style>
        <h1>LocalCam：首次信任设置</h1><p>这是 Windows 电脑临时提供的本地页面，不会访问互联网。</p>
        <p><a href="/certificate/localcam-ca.cer">下载 LocalCam 本地 CA 证书</a></p>
        <ol><li>下载 LocalCam 证书描述文件。</li><li>打开 iPhone 的“设置 → 通用 → VPN 与设备管理”，完成安装。</li><li><strong>安装后还必须</strong>到“设置 → 通用 → 关于本机 → 证书信任设置”，为 <strong>LocalCam Local CA</strong> 开启完全信任。</li><li>返回 Windows 中的 LocalCam 页面，重新生成并扫描“开始连接”二维码。</li></ol>
        <p><strong>若 Safari 显示“此连接非私人连接”，说明上面的“完全信任”尚未生效。不要绕过警告继续访问，请检查证书信任开关后重新打开连接页面。</strong></p>
        <p>该证书仅用于让此电脑在局域网内以 HTTPS 提供 LocalCam 页面；不要在不受信任的设备上安装。</p></html>
        """;

    public const string Monitor = """
        <!doctype html><html lang="zh-CN"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>LocalCam 本地预览</title>
        <style>
        :root{color-scheme:dark;font-family:"Segoe UI",system-ui,sans-serif;background:#10151f;color:#eef4ff}body{margin:0}.shell{max-width:1180px;margin:auto;padding:26px}.grid{display:grid;grid-template-columns:minmax(0,1.6fr) minmax(300px,1fr);gap:22px}.card{background:#192231;border:1px solid #314259;border-radius:14px;padding:18px}.video{aspect-ratio:16/9;background:#05080e;display:grid;place-items:center;border-radius:10px;overflow:hidden}.video img{width:100%;height:100%;object-fit:contain}.muted{color:#aebbcf}.status{color:#78dba2}.qr{width:220px;max-width:100%;background:#fff;padding:9px;border-radius:8px}.url{font-family:Consolas,monospace;font-size:.82rem;overflow-wrap:anywhere;background:#0d131d;padding:8px;border-radius:6px}.controls{display:flex;gap:9px;flex-wrap:wrap;margin:12px 0}.controls button{flex:1;min-width:110px}button{padding:10px 14px;border:0;border-radius:7px;background:#4c8dff;color:white;font-weight:600;cursor:pointer}@media(max-width:800px){.grid{grid-template-columns:1fr}}</style>
        <main class="shell"><h1>LocalCam <small class="muted">v0.1 本地预览</small></h1><div class="grid"><section class="card"><div class="video"><img id="preview" alt="尚未收到 iPhone 画面"><span id="empty" class="muted">等待 iPhone 摄像头连接</span></div><p id="streamState" class="status">预览服务已就绪</p><p id="metrics" class="muted">尚未收到视频帧</p><div class="controls"><button id="rearCamera">后置摄像头</button><button id="frontCamera">前置摄像头</button><button id="fps20">20 FPS</button><button id="fps30">30 FPS</button></div><div class="controls"><button id="fitFrame">完整显示</button><button id="fillFrame">铺满窗口</button></div><label>缩放 <input id="zoom" type="range" min="0.5" max="10" step="0.1" value="1"> <output id="zoomValue">1.0×</output></label><p class="muted">“完整显示”不裁边；“铺满窗口”会裁掉边缘。缩放范围由 iPhone 当前镜头决定。</p></section><aside class="card"><h2>连接 iPhone</h2><p class="muted">首次使用时，先扫描并安装本地 CA；以后无需重复。</p><img id="bootstrapQr" class="qr" alt="安装证书二维码"><p><button id="copyBootstrap">复制证书链接</button></p><p class="url" id="bootstrapUrl"></p><hr><p>信任完成后扫描：</p><img id="phoneQr" class="qr" alt="开始连接二维码"><p><button id="copyPhone">复制连接链接</button></p><p class="url" id="phoneUrl"></p><p id="pairState" class="muted"></p></aside></div></main>
        <script>
        const ui={preview:document.querySelector('#preview'),empty:document.querySelector('#empty'),metrics:document.querySelector('#metrics'),pairState:document.querySelector('#pairState'),streamState:document.querySelector('#streamState')};let objectUrl,monitorSocket;let received=0;let last=performance.now();
        async function refreshPair(){const response=await fetch('/api/session',{cache:'no-store'});const data=await response.json();for(const k of ['bootstrap','phone']){document.querySelector('#'+k+'Qr').src=data[k+'Qr'];document.querySelector('#'+k+'Url').textContent=data[k+'Url'];document.querySelector('#copy'+k[0].toUpperCase()+k.slice(1)).onclick=()=>navigator.clipboard.writeText(data[k+'Url']);}ui.pairState.textContent='配对链接有效至 '+new Date(data.expiresAt).toLocaleTimeString();}
        function sendControl(control){if(monitorSocket?.readyState!==WebSocket.OPEN){ui.streamState.textContent='尚未连接到 iPhone';return;}monitorSocket.send(JSON.stringify(control));ui.streamState.textContent='已发送控制指令';}
        let zoomTimer;const zoom=document.querySelector('#zoom'),zoomValue=document.querySelector('#zoomValue');document.querySelector('#rearCamera').onclick=()=>sendControl({type:'camera.switch',facing:'environment'});document.querySelector('#frontCamera').onclick=()=>sendControl({type:'camera.switch',facing:'user'});document.querySelector('#fps20').onclick=()=>sendControl({type:'capture.fps',fps:20});document.querySelector('#fps30').onclick=()=>sendControl({type:'capture.fps',fps:30});document.querySelector('#fitFrame').onclick=()=>ui.preview.style.objectFit='contain';document.querySelector('#fillFrame').onclick=()=>ui.preview.style.objectFit='cover';zoom.oninput=()=>{zoomValue.textContent=Number(zoom.value).toFixed(1)+'×';clearTimeout(zoomTimer);zoomTimer=setTimeout(()=>sendControl({type:'camera.zoom',zoom:Number(zoom.value)}),60)};
        async function refreshPhoneState(){try{const state=await fetch('/api/status',{cache:'no-store'}).then(r=>r.json());if(!state.phoneConnected){ui.streamState.textContent='iPhone 未连接';ui.preview.removeAttribute('src');ui.empty.hidden=false;ui.metrics.textContent='尚未收到视频帧'}}catch{}}function monitor(){monitorSocket=new WebSocket((location.protocol==='https:'?'wss':'ws')+'://'+location.host+'/ws/monitor');monitorSocket.binaryType='arraybuffer';monitorSocket.onopen=()=>ui.streamState.textContent='正在等待 iPhone';monitorSocket.onmessage=e=>{if(objectUrl)URL.revokeObjectURL(objectUrl);objectUrl=URL.createObjectURL(new Blob([e.data],{type:'image/jpeg'}));ui.preview.src=objectUrl;ui.empty.hidden=true;received++;const now=performance.now();if(now-last>1000){ui.metrics.textContent='正在接收 JPEG 预览：约 '+received+' 帧/秒（v0.1 验证通道）';received=0;last=now;}};monitorSocket.onclose=()=>{ui.streamState.textContent='预览连接重连中…';setTimeout(monitor,1000)}}refreshPair().catch(e=>ui.pairState.textContent='生成配对信息失败：'+e.message);monitor();setInterval(refreshPhoneState,500);
        </script>
        """;

    public const string Phone = """
        <!doctype html><html lang="zh-CN"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
        <title>LocalCam MediaRecorder PoC</title>
        <style>:root{color-scheme:dark;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;background:#111827;color:#f8fafc}body{margin:0;padding:16px}.card{max-width:560px;margin:auto}video{width:100%;background:#020617;border-radius:12px;aspect-ratio:16/9;object-fit:cover}.controls{display:flex;gap:8px;margin:8px 0}.controls button{flex:1;font-size:15px;padding:10px;border-radius:8px;border:0;color:#fff;font-weight:600;cursor:pointer}button#start{font-size:16px;padding:12px;border-radius:9px;border:0;background:#3478f6;color:#fff;font-weight:700;width:100%;margin:10px 0}.note{color:#b4c0d4;line-height:1.5;margin:6px 0}.mono{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:13px}.ok{color:#68d391}.highlight{color:#38bdf8}.warn{color:#fde047}</style>
        <main class="card">
        <h1>LocalCam <small style="font-size:14px;color:#38bdf8">MediaRecorder PoC</small></h1>
        <video id="camera" autoplay muted playsinline></video>
        <div class="controls">
          <button id="btnH264" style="background:#2563eb">H.264 (AVC)</button>
          <button id="btnHEVC" style="background:#374151">HEVC (H.265)</button>
        </div>
        <div class="controls"><button id="rear" style="background:#4b5563">后置</button><button id="front" style="background:#4b5563">前置</button></div>
        <label>缩放 <input id="zoom" type="range" min="0.5" max="10" step="0.1" value="1"> <output id="zoomValue">1.0×</output></label>
        <button id="start">开始 PoC 传输（1080p20）</button>
        <p id="status" class="note">正在初始化…</p>
        <p id="trackInfo" class="note mono warn"></p>
        <p id="codecInfo" class="note mono highlight"></p>
        <p id="metrics" class="note mono ok">等待连接…</p>
        </main>
        <script>
        const params=new URLSearchParams(location.search), session=params.get('session'), token=params.get('token');
        const camera=document.querySelector('#camera'), status=document.querySelector('#status'), start=document.querySelector('#start'), zoom=document.querySelector('#zoom'), zoomValue=document.querySelector('#zoomValue');
        const trackInfo=document.querySelector('#trackInfo'), codecInfo=document.querySelector('#codecInfo'), metrics=document.querySelector('#metrics');
        let ws,stream,facing='environment',targetFps=20,recorder;
        let preferredCodecFamily=params.get('codec')==='hevc'?'hevc':'h264';
        const h264Candidates=['video/mp4;codecs=avc1','video/mp4;codecs=avc1.42E01E','video/mp4;codecs=avc1.4D401F','video/mp4;codecs=avc1.640028','video/mp4'];
        const hevcCandidates=['video/mp4;codecs=hvc1','video/mp4;codecs=hevc'];
        const supportedH264=typeof MediaRecorder!=='undefined'?h264Candidates.filter(t=>MediaRecorder.isTypeSupported(t)):[];
        const supportedHEVC=typeof MediaRecorder!=='undefined'?hevcCandidates.filter(t=>MediaRecorder.isTypeSupported(t)):[];
        function getSelectedMime(){if(preferredCodecFamily==='hevc'&&supportedHEVC.length>0)return supportedHEVC[0];if(supportedH264.length>0)return supportedH264[0];return 'video/mp4';}
        function updateCodecButtons(){document.querySelector('#btnH264').style.background=preferredCodecFamily==='h264'?'#2563eb':'#374151';document.querySelector('#btnHEVC').style.background=preferredCodecFamily==='hevc'?'#16a34a':'#374151';}
        function setStatus(t,good=false){status.textContent=t;status.className=good?'ok':'note'}
        let rvfcFrames=0,rvfcStart=performance.now(),captureFps='0.0';
        function onVideoFrame(now){rvfcFrames++;const dt=(now-rvfcStart)/1000;if(dt>=1.0){captureFps=(rvfcFrames/dt).toFixed(1);rvfcFrames=0;rvfcStart=now;}if(camera.requestVideoFrameCallback)camera.requestVideoFrameCallback(onVideoFrame);}
        function getTrackDetails(){const track=stream?.getVideoTracks()[0];if(!track)return{w:0,h:0,fps:0};const s=track.getSettings?track.getSettings():{};return{w:s.width||camera.videoWidth||0,h:s.height||camera.videoHeight||0,fps:s.frameRate||0};}
        async function openCamera(){if(stream)stream.getTracks().forEach(track=>track.stop());stream=await navigator.mediaDevices.getUserMedia({video:{facingMode:{ideal:facing},width:{ideal:1920},height:{ideal:1080},frameRate:{ideal:targetFps,max:30}},audio:false});try{localStorage.setItem('localcam.cameraAllowed','1')}catch{}camera.srcObject=stream;await camera.play();if(camera.requestVideoFrameCallback)camera.requestVideoFrameCallback(onVideoFrame);configureZoom();}
        function configureZoom(){const capabilities=stream?.getVideoTracks()[0]?.getCapabilities?.();if(!capabilities?.zoom){zoom.disabled=true;return;}zoom.disabled=false;zoom.min=capabilities.zoom.min;zoom.max=capabilities.zoom.max;zoom.step=capabilities.zoom.step||0.1;zoom.value=Math.min(Math.max(1,capabilities.zoom.min),capabilities.zoom.max);zoomValue.textContent=Number(zoom.value).toFixed(1)+'×';}
        async function setZoom(value){const track=stream?.getVideoTracks()[0],capabilities=track?.getCapabilities?.();if(!track||!capabilities?.zoom)return;const effectiveZoom=Math.min(Math.max(value,capabilities.zoom.min),capabilities.zoom.max);await track.applyConstraints({advanced:[{zoom:effectiveZoom}]});zoom.value=effectiveZoom;zoomValue.textContent=effectiveZoom.toFixed(1)+'×';}
        let lastChunkTime=performance.now(),lastChunkInterval=0,chunkCount=0,lastChunkBytes=0,sessionBytes=0,sessionStart=performance.now(),windowBytes=0,windowStart=performance.now(),phoneMbps='0.0';
        function sendMeta(){if(ws&&ws.readyState===WebSocket.OPEN&&recorder){const d=getTrackDetails();ws.send(JSON.stringify({type:'poc.meta',family:preferredCodecFamily,mimeType:recorder.mimeType,configuredMime:getSelectedMime(),vBitrate:recorder.videoBitsPerSecond||0,trackWidth:d.w,trackHeight:d.h,trackFps:d.fps,supportedH264,supportedHEVC}));}}
        function startRecording(){if(recorder&&recorder.state!=='inactive'){try{recorder.stop();}catch(e){}}const mime=getSelectedMime();try{recorder=new MediaRecorder(stream,{mimeType:mime});}catch(err){setStatus('MediaRecorder 初始化失败: '+(err.message||err.name));return;}lastChunkTime=performance.now();sessionStart=performance.now();windowStart=sessionStart;chunkCount=0;sessionBytes=0;windowBytes=0;recorder.ondataavailable=e=>{if(!e.data||e.data.size===0)return;const now=performance.now();lastChunkInterval=now-lastChunkTime;lastChunkTime=now;lastChunkBytes=e.data.size;sessionBytes+=e.data.size;windowBytes+=e.data.size;chunkCount++;if(ws&&ws.readyState===WebSocket.OPEN)ws.send(e.data);};recorder.start(50);sendMeta();}
        async function switchCamera(nextFacing){facing=nextFacing;try{await openCamera();if(ws&&ws.readyState===WebSocket.OPEN)startRecording();setStatus((facing==='user'?'前置':'后置')+'摄像头已切换',true);}catch(error){setStatus('切换摄像头失败：'+(error.message||error.name));}}
        async function begin(){if(!window.isSecureContext){setStatus('此页面不是可信 HTTPS，请先完成证书信任。');return;}start.disabled=true;try{await openCamera();ws=new WebSocket('wss://'+location.host+'/ws/phone?session='+encodeURIComponent(session)+'&token='+encodeURIComponent(token));ws.binaryType='arraybuffer';ws.onopen=()=>{setStatus('已连接到 Windows，MediaRecorder 传输中',true);startRecording();};ws.onerror=()=>setStatus('无法建立连接：二维码可能已过期，请在 Windows 端重新生成。');ws.onclose=()=>{if(recorder&&recorder.state!=='inactive')try{recorder.stop();}catch(e){}if(stream)stream.getTracks().forEach(t=>t.stop());setStatus('连接已关闭。');};}catch(error){start.disabled=false;setStatus('无法启动：'+(error.message||error.name));}}
        setInterval(()=>{const now=performance.now(),dt=(now-windowStart)/1000;if(dt>=1.0){phoneMbps=((windowBytes*8)/(dt*1000000)).toFixed(2);windowBytes=0;windowStart=now;}const d=getTrackDetails(),elapsedSec=Math.floor((now-sessionStart)/1000),mm=String(Math.floor(elapsedSec/60)).padStart(2,'0'),ss=String(elapsedSec%60).padStart(2,'0'),stableTag=elapsedSec>=120?' ✓ 2分稳定':'';if(trackInfo)trackInfo.textContent='相机源: '+d.w+'×'+d.h+' @ '+(d.fps||'--')+' (实际采集: '+captureFps+' fps)';if(codecInfo&&recorder)codecInfo.textContent='Codec: '+recorder.mimeType+' | 配置bps: '+(recorder.videoBitsPerSecond||'默认');if(metrics){if(ws&&ws.readyState===WebSocket.OPEN&&recorder&&recorder.state==='recording'){metrics.textContent='码率: '+phoneMbps+' Mbps | 块间隔: '+lastChunkInterval.toFixed(0)+' ms | 块大小: '+(lastChunkBytes/1024).toFixed(1)+' KB | 总块数: '+chunkCount+' | 运行: '+mm+':'+ss+stableTag;metrics.style.color=elapsedSec>=120?'#68d391':'#38bdf8';}else{metrics.textContent='等待传输数据…';metrics.style.color='#b4c0d4';}}},500);
        document.querySelector('#btnH264').onclick=()=>{preferredCodecFamily='h264';updateCodecButtons();if(ws&&ws.readyState===WebSocket.OPEN)startRecording();};
        document.querySelector('#btnHEVC').onclick=()=>{preferredCodecFamily='hevc';updateCodecButtons();if(ws&&ws.readyState===WebSocket.OPEN)startRecording();};
        zoom.oninput=()=>{zoomValue.textContent=Number(zoom.value).toFixed(1)+'×';setZoom(Number(zoom.value));};
        document.querySelector('#rear').onclick=()=>switchCamera('environment');
        document.querySelector('#front').onclick=()=>switchCamera('user');
        start.onclick=begin;
        updateCodecButtons();
        (async function autoStart(){let should=false;try{should=localStorage.getItem('localcam.cameraAllowed')==='1';}catch{}try{if(navigator.permissions?.query){const p=await navigator.permissions.query({name:'camera'});should=should||p.state==='granted';}}catch{}if(should)await begin();})();
        </script>
        """;
}
