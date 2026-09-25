using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        Console.WriteLine("Starting named pipe server...");
        using var pipeServer = new NamedPipeServerStream("testpipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        
        var ffmpeg = new Process {
            StartInfo = new ProcessStartInfo {
                FileName = @"bin\Debug\net10.0-windows\ffmpeg.exe",
                Arguments = "-hide_banner -f lavfi -i testsrc=duration=1:size=640x480:rate=10 -f rawvideo -y \\\\.\\pipe\\testpipe",
                UseShellExecute = false
            }
        };
        ffmpeg.Start();

        Console.WriteLine("Waiting for connection...");
        await pipeServer.WaitForConnectionAsync();
        Console.WriteLine("Connected!");
        
        var buffer = new byte[1024];
        var bytesRead = await pipeServer.ReadAsync(buffer, 0, buffer.Length);
        Console.WriteLine($"Read {bytesRead} bytes.");
    }
}
