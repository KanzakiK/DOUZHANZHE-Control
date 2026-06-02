using System;
using System.Runtime.InteropServices;
using System.Threading;
class EcReader
{
    [DllImport("WinRing0x64.dll",CallingConvention=CallingConvention.Cdecl)] static extern bool InitializeOls();
    [DllImport("WinRing0x64.dll",CallingConvention=CallingConvention.Cdecl)] static extern void DeinitializeOls();
    [DllImport("WinRing0x64.dll",CallingConvention=CallingConvention.Cdecl)] static extern byte ReadIoPortByte(ushort p);
    [DllImport("WinRing0x64.dll",CallingConvention=CallingConvention.Cdecl)] static extern void WriteIoPortByte(ushort p, byte v);
    static void S(int m=2){Thread.Sleep(m);}
    static byte R(byte a){WriteIoPortByte(0x66,0x80);S();WriteIoPortByte(0x62,a);S(5);return ReadIoPortByte(0x62);}
    static int Read16(byte h,byte l){int v=(R(h)<<8)|R(l);Thread.Sleep(4);return v;}
    static int ReadFan(byte h,byte l,int mR)
    {
        for(int i=0;i<10;i++){if(i>0)Thread.Sleep(15);int v=Read16(h,l);if(v>50&&v<=mR)return v;}
        return 0;
    }
    static int ReadTemp()
    {
        for(int i=0;i<5;i++){if(i>0)Thread.Sleep(10);int v=R(0x1C);if(v>20&&v<120)return v;}
        return 0;
    }
    static int Main(string[] args)
    {
        if(!InitializeOls()){Console.Write("INITFAIL");return 1;}
        try
        {
            string m=args.Length>0?args[0]:"";
            int r=0;
            if(m=="cpu") r=ReadFan(0x9D,0x9E,4400);
            else if(m=="gpu") r=ReadFan(0x96,0x97,8200);
            else if(m=="temp") r=ReadTemp();
            Console.Write(r.ToString());
            return r>0?0:2;
        }
        catch(Exception ex){Console.Write("ERR:"+ex.Message);return 2;}
        finally{DeinitializeOls();}
    }
}