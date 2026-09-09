#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>
#include <fstream>
#include <stdexcept>
#include <chrono>
#include <algorithm>
#include <cmath>
extern "C" __declspec(dllimport) int dc_create(int,int,void**,int*);
extern "C" __declspec(dllimport) int dc_decode(void*,const uint8_t*,int,int64_t,int,uint8_t*,int,int*);
extern "C" __declspec(dllimport) void dc_destroy(void*);
extern "C" __declspec(dllimport) int dc_render(const uint8_t*,int,int,int,double,double,double,double,uint8_t*,int,int,uint8_t*,int*);
static void check(bool ok,const char* msg){if(!ok)throw std::runtime_error(msg);}
static void verifyPortraitGeometry(){
    const int ow=1920,oh=1080;
    std::vector<uint8_t> out(ow*oh*3/2),bgra(ow*oh*4);
    for(int portrait=0;portrait<2;++portrait){
        const int w=portrait?1080:1920,h=portrait?1920:1080;
        std::vector<uint8_t> raw(w*h*3/2,128);
        std::fill(raw.begin(),raw.begin()+w*h,32);
        for(int y=0;y<h;++y)for(int x=0;x<w;++x)
            if((x-w/2)*(x-w/2)+(y-h/2)*(y-h/2)<=100*100)raw[y*w+x]=235;
        for(int rotation=0;rotation<4;++rotation){
            int rect[4]{};
            check(dc_render(raw.data(),w,h,rotation,0,0,1,1,out.data(),ow,oh,bgra.data(),rect)>=0,"Portrait render failed");
            int rw=(rotation&1)?h:w,rh=(rotation&1)?w:h;
            check(std::abs(rect[2]*rh-rect[3]*rw)<=2*std::max(rw,rh),"Output aspect ratio changed");
            int minX=ow,maxX=-1,minY=oh,maxY=-1;
            for(int y=0;y<oh;++y)for(int x=0;x<ow;++x)if(out[y*ow+x]>225){
                minX=std::min(minX,x);maxX=std::max(maxX,x);minY=std::min(minY,y);maxY=std::max(maxY,y);
            }
            check(maxX-minX>100 && std::abs((maxX-minX)-(maxY-minY))<=2,"Circle distorted by native rotation");
            // Desktop previews retain all source pixels, even when the webcam needs letterboxing.
            check(dc_render(raw.data(),w,h,rotation,0,0,1,1,out.data(),rw,rh,bgra.data(),rect)>=0,"Native-size preview failed");
            check(rect[0]==0 && rect[1]==0 && rect[2]==rw && rect[3]==rh,"Native-size preview lost resolution");
            std::vector<uint8_t> camera(ow*oh*3/2);
            check(dc_render(out.data(),rw,rh,0,0,0,1,1,camera.data(),ow,oh,nullptr,rect)>=0,"NV12-only camera output failed");
            check(std::abs(rect[2]*rh-rect[3]*rw)<=2*std::max(rw,rh),"Camera output stretched native preview");
        }
    }
    std::puts("PASS: portrait/landscape circles preserve aspect ratio in all 4 rotations");
}
int main(int argc,char** argv){
 try{
    check(argc==2,"Usage: DeskCamVideoTest fixture.dcv");
    std::ifstream file(argv[1],std::ios::binary);check(!!file,"Cannot open fixture");
    const int w=1920,h=1080;
    std::vector<uint8_t> raw(w*h*3/2),out(w*h*3/2),bgra(w*h*4);
    void* decoder=nullptr;int hardware=0;
    int hr=dc_create(w,h,&decoder,&hardware);
    std::printf("create=%08x hardware=%d\n",hr,hardware);check(hr>=0,"Decoder create failed");
    auto start=std::chrono::steady_clock::now();int frames=0,packets=0;
    while(true){
        uint8_t header[32];file.read(reinterpret_cast<char*>(header),32);if(file.gcount()==0)break;
        check(file.gcount()==32,"Truncated header");
        uint32_t len=0,flags=0;int64_t timestamp=0;
        std::memcpy(&len,header+28,4);std::memcpy(&flags,header+24,4);std::memcpy(&timestamp,header+16,8);
        check(len<2*1024*1024,"Invalid length");std::vector<uint8_t> data(len);
        file.read(reinterpret_cast<char*>(data.data()),len);check(file.gcount()==len,"Truncated payload");
        int produced=0;hr=dc_decode(decoder,data.data(),len,timestamp,flags&1,raw.data(),static_cast<int>(raw.size()),&produced);
        if(hr<0)std::printf("decode packet %d = %08x\n",packets,hr);
        check(hr>=0,"Decode failed");packets++;
        if(!produced)continue;
        frames++;
        int rect[4]{};
        check(dc_render(raw.data(),w,h,0,0,0,1,1,out.data(),w,h,bgra.data(),rect)>=0,"Render failed");
        check(rect[0]==0&&rect[1]==0&&rect[2]==w&&rect[3]==h,"Identity changed geometry");
        check(out[(h/4)*w+w/4]>45&&out[(h/4)*w+w/4]<60,"Decoded top-left luma wrong");
        check(out[(h*3/4)*w+w*3/4]>195&&out[(h*3/4)*w+w*3/4]<215,"Decoded bottom-right luma wrong");
    }
    dc_destroy(decoder);
    check(frames>=38,"Decoder buffers or drops too many frames");
    int rect[4]{};
    // Crop to a uniform quadrant. Its gray value must fill the output, independent of camera size.
    check(dc_render(raw.data(),w,h,0,.05,.05,.35,.35,out.data(),w,h,bgra.data(),rect)>=0,"Crop failed");
    check(out[(h/2)*w+w/2]<65,"Crop selected wrong region");
    // Clockwise rotation moves the bottom-left quadrant to the top-left of the portrait image.
    check(dc_render(raw.data(),w,h,1,0,0,1,1,out.data(),w,h,bgra.data(),rect)>=0,"Rotation failed");
    int px=rect[0]+rect[2]/4,py=rect[1]+rect[3]/4;
    check(out[py*w+px]>140&&out[py*w+px]<170,"Rotation orientation wrong");
    check(dc_render(raw.data(),w,h,0,-.1,0,1,1,out.data(),w,h,bgra.data(),rect)<0,"Invalid crop was accepted");
    // A fresh session must decode the first keyframe again after a disconnect.
    check(dc_create(w,h,&decoder,&hardware)>=0,"Recreate failed");dc_destroy(decoder);
    verifyPortraitGeometry();
    double seconds=std::chrono::duration<double>(std::chrono::steady_clock::now()-start).count();
    std::printf("PASS: %d/%d frames, decode+render %.1f fps, crop/rotation/recreate verified\n",frames,packets,frames/seconds);
    return 0;
 }catch(const std::exception& ex){std::fprintf(stderr,"FAIL: %s\n",ex.what());return 1;}
}
