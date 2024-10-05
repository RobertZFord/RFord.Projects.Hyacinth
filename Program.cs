using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

string root = Environment.CurrentDirectory;

if (args.Length == 1)
{
    if (Directory.Exists(args[0]) == false)
    {
        Console.WriteLine("Unable to access directory '{0}'!", args[0]);
        return;
    }
    root = args[0];
}

Console.WriteLine("Hosting directory: {0}", root);

IFileProvider _fileProvider = new PhysicalFileProvider(
    root: root,
    filters: ExclusionFilters.DotPrefixed |
             ExclusionFilters.Hidden |
             ExclusionFilters.System |
             ExclusionFilters.Sensitive
);

TcpListener tcpListener = new TcpListener(
    localaddr: IPAddress.Any,
    port: 1900
);

// listen on 0.0.0.0:1900
tcpListener.Start();

while (tcpListener.Server.IsBound)
{
    // wait for a connection
    //  `cat`ing a 3MB into `netcat` results in about 124096 bytes
    // available vs the 131072 buffer, yielding 6976 bytes of overhead
    TcpClient tcp = await tcpListener.AcceptTcpClientAsync();
    _ = ThreadPool.QueueUserWorkItem(Handler, tcp);
}

// supporting methods

async void Handler(object? state)
{
    // async voids are bad, so we want to catch anything that might explode the program!
    try
    {
        if (state is not TcpClient tcp)
        {
            if (state is IDisposable disposable)
            {
                // at least try to prevent any runaway memory leaks
                disposable.Dispose();
            }
            return;
        }

        await InternalHandler(tcp);

        tcp.Dispose();
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            "An error occurred in the thread handler!  Error message: {0}",
            ex.Message
        );
    }
}

async Task InternalHandler(TcpClient tcp)
{
    using (NetworkStream stream = tcp.GetStream())
    {
        try
        {
            string req = "";
            if (tcp.Available > 0)
            {
                // discard large requests
                if (tcp.Available > 16_384)
                {
                    Console.WriteLine("Ignoring oversized request!");
                    return;
                }

                byte[] b = new byte[tcp.Available];

                int q = await stream.ReadAsync(
                    buffer: b,
                        offset: 0,
                        count: tcp.Available
                );

                req = Encoding.ASCII
                                .GetString(b)
                                // normalize some
                                .Trim(['\n', '\r', '/'])
                                ;
            }

            Console.WriteLine("Handling request for '{0}'", req);

            await WriteResponse(
                path: req,
                stream: stream
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine("An error occurred while processing a request!  Error message: {0}", ex.Message);
        }
        finally
        {
            tcp.Close();
            stream.Close();
        }
    }

    tcp.Dispose();
}

async Task WriteResponse(string path, Stream stream)
{
    static async Task writeFileToOutput(IFileInfo fileInfo, Stream outputStream)
    {
        using (Stream fileStream = fileInfo.CreateReadStream())
        {
            await fileStream.CopyToAsync(outputStream);
        }
    }

    // if it's a specific file, just return it
    if (_fileProvider.GetFileInfo(subpath: path) is IFileInfo file && file.Exists)
    {
        await writeFileToOutput(fileInfo: file, outputStream: stream);
        return;
    }

    // if we didn't have a specific file, try loading the index file for a given path
    if (_fileProvider.GetFileInfo(subpath: $"{path}/index") is IFileInfo index && index.Exists)
    {
        await writeFileToOutput(fileInfo: index, outputStream: stream);
        return;
    }

    // no file, no index, maybe it's a directory?
    if (_fileProvider.GetDirectoryContents(path) is IDirectoryContents directoryContents && directoryContents.Exists)
    {
        using (StreamWriter sw = new StreamWriter(stream))
        {
            foreach (IFileInfo fileInfo in directoryContents)
            {
                string output = string.Format(
                    "=> {0}{1}",
                    fileInfo.Name,
                    fileInfo.IsDirectory ? "/" : ""
                );
                await sw.WriteLineAsync(output);
            }
        }
    }

    // welp
}
