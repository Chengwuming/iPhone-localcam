#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mftransform.h>
#include <codecapi.h>
#include <wmcodecdsp.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <vector>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
#define CHECK(x) do { HRESULT hr_ = (x); if (FAILED(hr_)) return hr_; } while(0)

// All methods on a decoder are called by one MTA worker. Its output is owned by the caller.
struct Decoder {
    ComPtr<IMFTransform> transform;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IMFDXGIDeviceManager> manager;
    ComPtr<ID3D11Texture2D> staging;
    int width=0,height=0,codedHeight=0,stride=0;
    bool hardware=false, mfStarted=false, comStarted=false;
    HRESULT outputType() {
        for (DWORD i=0;;++i) {
            ComPtr<IMFMediaType> type;
            HRESULT hr=transform->GetOutputAvailableType(0,i,&type);
            if (FAILED(hr)) return hr;
            GUID subtype{};
            if (SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE,&subtype)) && subtype==MFVideoFormat_NV12) {
                CHECK(transform->SetOutputType(0,type.Get(),0));
                UINT32 w=0,h=0;
                CHECK(MFGetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,&w,&h));
                if (w<static_cast<UINT32>(width)||h<static_cast<UINT32>(height)||w>4096||h>4096)
                    return MF_E_INVALIDMEDIATYPE;
                codedHeight=static_cast<int>(h);
                stride=static_cast<int>(MFGetAttributeUINT32(type.Get(),MF_MT_DEFAULT_STRIDE,w));
                return S_OK;
            }
        }
    }
    HRESULT initialize(int w,int h) {
        width=w;height=h;
        HRESULT co=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
        if (FAILED(co)) return co;
        comStarted=true;
        CHECK(MFStartup(MF_VERSION,MFSTARTUP_LITE));mfStarted=true;
        CHECK(CoCreateInstance(CLSID_CMSH264DecoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&transform)));
        ComPtr<ICodecAPI> codec;
        if(SUCCEEDED(transform.As(&codec))) {
            VARIANT value;VariantInit(&value);value.vt=VT_UI4;value.ulVal=1;
            codec->SetValue(&CODECAPI_AVLowLatencyMode,&value);
        }
        // Enable DXVA when this system decoder exposes D3D11 support.
        ComPtr<IMFAttributes> attrs;
        if(SUCCEEDED(transform->GetAttributes(&attrs)) && MFGetAttributeUINT32(attrs.Get(),MF_SA_D3D11_AWARE,0)) {
            D3D_FEATURE_LEVEL level{};
            if(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,
                D3D11_CREATE_DEVICE_VIDEO_SUPPORT|D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,
                &device,&level,&context))) {
                UINT token=0;
                if(SUCCEEDED(MFCreateDXGIDeviceManager(&token,&manager)) &&
                    SUCCEEDED(manager->ResetDevice(device.Get(),token)) &&
                    SUCCEEDED(transform->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,reinterpret_cast<ULONG_PTR>(manager.Get()))))
                    hardware=true;
            }
        }
        ComPtr<IMFMediaType> input;
        CHECK(MFCreateMediaType(&input));
        CHECK(input->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video));
        CHECK(input->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_H264));
        CHECK(input->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive));
        CHECK(MFSetAttributeSize(input.Get(),MF_MT_FRAME_SIZE,w,h));
        CHECK(MFSetAttributeRatio(input.Get(),MF_MT_FRAME_RATE,20,1));
        CHECK(transform->SetInputType(0,input.Get(),0));
        CHECK(outputType());
        CHECK(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,0));
        return transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0);
    }
    HRESULT copy(IMFSample* sample,uint8_t* destination,int capacity) {
        if(capacity<width*height*3/2)return E_INVALIDARG;
        ComPtr<IMFMediaBuffer> buffer;
        CHECK(sample->GetBufferByIndex(0,&buffer));
        auto copyRows=[&](const uint8_t* y,int pitch,int uvHeight) {
            for(int row=0;row<height;++row)std::memcpy(destination+row*width,y+row*pitch,width);
            auto uv=y+uvHeight*pitch;
            for(int row=0;row<height/2;++row)std::memcpy(destination+width*height+row*width,uv+row*pitch,width);
        };
        ComPtr<IMFDXGIBuffer> dxgi;
        if(SUCCEEDED(buffer.As(&dxgi))) {
            ComPtr<ID3D11Texture2D> texture;
            CHECK(dxgi->GetResource(IID_PPV_ARGS(&texture)));
            UINT subresource=0;CHECK(dxgi->GetSubresourceIndex(&subresource));
            D3D11_TEXTURE2D_DESC desc{};texture->GetDesc(&desc);
            if(desc.Format!=DXGI_FORMAT_NV12)return MF_E_INVALIDMEDIATYPE;
            D3D11_TEXTURE2D_DESC old{};if(staging)staging->GetDesc(&old);
            if(!staging||old.Width!=desc.Width||old.Height!=desc.Height) {
                staging.Reset();
                desc.ArraySize=1;desc.MipLevels=1;desc.BindFlags=0;desc.MiscFlags=0;
                desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                CHECK(device->CreateTexture2D(&desc,nullptr,&staging));
            }
            context->CopySubresourceRegion(staging.Get(),0,0,0,0,texture.Get(),subresource,nullptr);
            D3D11_MAPPED_SUBRESOURCE mapped{};CHECK(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
            copyRows(static_cast<const uint8_t*>(mapped.pData),static_cast<int>(mapped.RowPitch),static_cast<int>(desc.Height));
            context->Unmap(staging.Get(),0);
            return S_OK;
        }
        ComPtr<IMF2DBuffer> twoD;
        if(SUCCEEDED(buffer.As(&twoD))) {
            BYTE* scan=nullptr;LONG pitch=0;CHECK(twoD->Lock2D(&scan,&pitch));
            if(pitch<width){twoD->Unlock2D();return MF_E_BUFFERTOOSMALL;}
            copyRows(scan,pitch,codedHeight);
            twoD->Unlock2D();return S_OK;
        }
        BYTE* data=nullptr;DWORD max=0,current=0;CHECK(buffer->Lock(&data,&max,&current));
        if(stride<width || current<static_cast<DWORD>(stride*codedHeight*3/2)){
            buffer->Unlock();return MF_E_BUFFERTOOSMALL;
        }
        copyRows(data,stride,codedHeight);buffer->Unlock();return S_OK;
    }
    HRESULT output(uint8_t* destination,int capacity,bool& produced) {
        for(int attempt=0;attempt<4;++attempt) {
            MFT_OUTPUT_STREAM_INFO info{};CHECK(transform->GetOutputStreamInfo(0,&info));
            ComPtr<IMFSample> sample;
            if(!(info.dwFlags&MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
                CHECK(MFCreateSample(&sample));
                ComPtr<IMFMediaBuffer> buffer;
                CHECK(MFCreateMemoryBuffer(std::max(info.cbSize,static_cast<DWORD>(stride*codedHeight*3/2)),&buffer));
                CHECK(sample->AddBuffer(buffer.Get()));
            }
            MFT_OUTPUT_DATA_BUFFER out{};out.pSample=sample.Get();
            DWORD status=0;
            HRESULT hr=transform->ProcessOutput(0,1,&out,&status);
            if(out.pEvents)out.pEvents->Release();
            if(!sample&&out.pSample)sample.Attach(out.pSample);
            if(hr==MF_E_TRANSFORM_STREAM_CHANGE){CHECK(outputType());continue;}
            if(hr==MF_E_TRANSFORM_NEED_MORE_INPUT)return S_OK;
            if(FAILED(hr))return hr;
            if(sample){CHECK(copy(sample.Get(),destination,capacity));produced=true;}
            return S_OK;
        }
        return E_UNEXPECTED;
    }
    HRESULT decode(const uint8_t* data,int length,int64_t timestamp,int flags,uint8_t* destination,int capacity,int* produced) {
        *produced=0;
        ComPtr<IMFSample> sample;CHECK(MFCreateSample(&sample));
        ComPtr<IMFMediaBuffer> buffer;CHECK(MFCreateMemoryBuffer(length,&buffer));
        BYTE* memory=nullptr;CHECK(buffer->Lock(&memory,nullptr,nullptr));
        std::memcpy(memory,data,length);buffer->Unlock();CHECK(buffer->SetCurrentLength(length));
        CHECK(sample->AddBuffer(buffer.Get()));CHECK(sample->SetSampleTime(timestamp*10));
        CHECK(sample->SetSampleDuration(500000));
        if(flags&1)sample->SetUINT32(MFSampleExtension_CleanPoint,TRUE);
        CHECK(transform->ProcessInput(0,sample.Get(),0));
        bool ready=false;CHECK(output(destination,capacity,ready));
        if(ready && (flags&6)) normalizeColor(destination,flags);
        *produced=ready?1:0;
        return S_OK;
    }
    void normalizeColor(uint8_t* frame,int flags) {
        auto clamp=[](int value){return static_cast<uint8_t>(std::clamp(value,0,255));};
        if(flags&4) {
            for(int i=0;i<width*height;++i)frame[i]=static_cast<uint8_t>(16+(frame[i]*219+127)/255);
            for(int i=width*height;i<width*height*3/2;++i)frame[i]=clamp(128+((static_cast<int>(frame[i])-128)*224)/255);
        }
        if(!(flags&2))return;
        // Normalize BT.601 to BT.709 once, keeping downstream preview and both camera formats consistent.
        for(int y=0;y<height;y+=2)for(int x=0;x<width;x+=2){
            int uv=width*height+(y/2)*width+x,u=frame[uv]-128,v=frame[uv+1]-128;
            int rs=0,gs=0,bs=0;
            for(int dy=0;dy<2;++dy)for(int dx=0;dx<2;++dx){
                int at=(y+dy)*width+x+dx,c=std::max(0,static_cast<int>(frame[at])-16);
                int r=clamp((298*c+409*v+128)>>8),g=clamp((298*c-100*u-208*v+128)>>8),b=clamp((298*c+516*u+128)>>8);
                frame[at]=clamp(((47*r+157*g+16*b+128)>>8)+16);rs+=r;gs+=g;bs+=b;
            }
            int r=rs/4,g=gs/4,b=bs/4;
            frame[uv]=clamp(((-26*r-87*g+112*b+128)>>8)+128);
            frame[uv+1]=clamp(((112*r-102*g-10*b+128)>>8)+128);
        }
    }
    ~Decoder(){
        if(transform)transform->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH,0);
        transform.Reset();staging.Reset();manager.Reset();context.Reset();device.Reset();
        if(mfStarted)MFShutdown();if(comStarted)CoUninitialize();
    }
};
API int dc_create(int w,int h,void** handle,int* hardware) {
    if(!handle||!hardware||w<16||h<16||w>3840||h>3840||w*h>3840*2160||(w&1)||(h&1))return E_INVALIDARG;
    *handle=nullptr;*hardware=0;
    auto decoder=new(std::nothrow) Decoder();if(!decoder)return E_OUTOFMEMORY;
    HRESULT hr=decoder->initialize(w,h);
    if(FAILED(hr)){delete decoder;return hr;}
    *hardware=decoder->hardware?1:0;*handle=decoder;return S_OK;
}
API int dc_decode(void* handle,const uint8_t* data,int length,int64_t timestamp,int key,uint8_t* out,int capacity,int* produced) {
    if(!handle||!data||!out||!produced||length<=0||length>2*1024*1024||timestamp<0||timestamp>INT64_MAX/10)return E_INVALIDARG;
    return static_cast<Decoder*>(handle)->decode(data,length,timestamp,key,out,capacity,produced);
}
API void dc_destroy(void* handle){delete static_cast<Decoder*>(handle);}
static uint8_t clampByte(int v){return static_cast<uint8_t>(std::clamp(v,0,255));}

// Transform NV12 planes directly; rotate coordinates before cropping. Output remains limited-range NV12.
API int dc_render(const uint8_t* input,int sw,int sh,int rotation,double cx,double cy,double cw,double ch,
    uint8_t* output,int ow,int oh,uint8_t* bgra,int* rect) {
    if(!input||!output||!rect||sw<16||sh<16||sw>3840||sh>3840||sw*sh>3840*2160||(sw&1)||(sh&1)||
        ow<16||oh<16||ow>3840||oh>3840||ow*oh>3840*2160||(ow&1)||(oh&1)||rotation<0||rotation>3||
        !std::isfinite(cx)||!std::isfinite(cy)||!std::isfinite(cw)||!std::isfinite(ch)||
        cx<0||cy<0||cw<0.01||ch<0.01||cx+cw>1.000001||cy+ch>1.000001)return E_INVALIDARG;
    const int rw=(rotation&1)?sh:sw,rh=(rotation&1)?sw:sh;
    const double aspect=rw*cw/(rh*ch);
    int dw=ow,dh=static_cast<int>(std::round(ow/aspect));
    if(dh>oh){dh=oh;dw=static_cast<int>(std::round(oh*aspect));}
    dw=std::clamp(dw&~1,2,ow);dh=std::clamp(dh&~1,2,oh);
    const int left=((ow-dw)/2)&~1,top=((oh-dh)/2)&~1;
    rect[0]=left;rect[1]=top;rect[2]=dw;rect[3]=dh;
    if(rotation==0 && cx==0 && cy==0 && cw==1 && ch==1 && sw==ow && sh==oh) {
        std::memcpy(output,input,ow*oh*3/2);
    } else {
        std::memset(output,16,ow*oh);std::memset(output+ow*oh,128,ow*oh/2);
        struct Axis { int first,second,fraction; };
        for(int plane=0;plane<2;++plane) {
            int unit=plane?2:1;
            int destW=dw/unit,destH=dh/unit,rotW=rw/unit,rotH=rh/unit;
            const auto src=input+(plane?sw*sh:0);
            auto dst=output+(plane?ow*oh:0)+(top/unit)*ow+left;
            std::vector<Axis> xx(destW),yy(destH);
            auto fillAxis=[](std::vector<Axis>& axis,int length,double start,double extent,int multiplier,bool reversed) {
                for(size_t i=0;i<axis.size();++i){
                    double position=start*length+(i+0.5)*extent*length/axis.size()-0.5;
                    if(reversed)position=length-1-position;
                    position=std::clamp(position,0.0,static_cast<double>(length-1));
                    int fixed=static_cast<int>(position*256.0),lo=fixed>>8,hi=std::min(lo+1,length-1);
                    axis[i]={lo*multiplier,hi*multiplier,fixed&255};
                }
            };
            fillAxis(xx,rotW,cx,cw,(rotation&1)?sw:unit,rotation==1||rotation==2);
            fillAxis(yy,rotH,cy,ch,(rotation&1)?unit:sw,rotation==2||rotation==3);
            for(int y=0;y<destH;++y) {
                const auto ya=yy[y];int fy=ya.fraction;
                const auto row0=src+ya.first,row1=src+ya.second;
                auto target=dst+y*ow;
                for(int x=0;x<destW;++x) {
                    const auto xa=xx[x];int fx=xa.fraction;
                    for(int c=0;c<unit;++c){
                        int a=row0[xa.first+c]*(256-fx)+row0[xa.second+c]*fx;
                        int b=row1[xa.first+c]*(256-fx)+row1[xa.second+c]*fx;
                        target[x*unit+c]=static_cast<uint8_t>((a*(256-fy)+b*fy+32768)>>16);
                    }
                }
            }
        }
    }
    // Canonical display and virtual camera output: BT.709 limited range.
    if(bgra)for(int y=0;y<oh;++y)for(int x=0;x<ow;++x){
        int l=std::max(0,static_cast<int>(output[y*ow+x])-16);
        int uv=ow*oh+(y/2)*ow+(x&~1),u=output[uv]-128,v=output[uv+1]-128;
        int p=(y*ow+x)*4;
        bgra[p]=clampByte((298*l+541*u+128)>>8);
        bgra[p+1]=clampByte((298*l-55*u-136*v+128)>>8);
        bgra[p+2]=clampByte((298*l+459*v+128)>>8);
        bgra[p+3]=255;
    }
    return S_OK;
}
