using System;
using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        var pipeName = "TestPipe_" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        
        Console.WriteLine("Pipe: " + pipeName);
        var args = "-y -f lavfi -i testsrc=d=1 -map 0:v -c:v rawvideo -pix_fmt uyvy422 -f fifo -fifo_format rawvideo -drop_pkts_on_overflow 1 -attempt_recovery 1 \\\\.\\pipe\\" + pipeName;
        Console.WriteLine("Args: " + args);

        var psi = new ProcessStartInfo(@".\bin\Debug\net10.0-windows\ffmpeg.exe", args);
        psi.UseShellExecute = false;
        psi.RedirectStandardError = true;
        var p = Process.Start(psi);
        
        var t = server.WaitForConnectionAsync();
        var completed = await Task.WhenAny(t, Task.Delay(2000));
        
        if (completed == t) Console.WriteLine("Connected!");
        else Console.WriteLine("Timeout!");
        
        Console.WriteLine("FFmpeg output: " + p.StandardError.ReadToEnd());
        p.Kill();
    }
}
