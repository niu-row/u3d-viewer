using System.IO.Pipes;
using System.Text.Json;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static int Main()
    {
        Console.WriteLine("U3D Viewer bootstrap client");
        Console.WriteLine("Waiting for a U3DViewer.Agent.Mono instance...");

        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    "u3d-viewer",
                    PipeDirection.In,
                    PipeOptions.None);

                pipe.Connect(1000);
                Console.WriteLine("Connected. Receiving live scene snapshots. Press Ctrl+C to exit.\n");

                using var reader = new StreamReader(pipe);
                while (pipe.IsConnected)
                {
                    var line = reader.ReadLine();
                    if (line is null) break;

                    var snapshot = JsonSerializer.Deserialize<SceneSnapshot>(line, JsonOptions);
                    if (snapshot is not null)
                    {
                        Render(snapshot);
                    }
                }
            }
            catch (TimeoutException)
            {
                Thread.Sleep(500);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Disconnected: {ex.Message}");
                Thread.Sleep(1000);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Invalid snapshot JSON: {ex.Message}");
            }
        }
    }

    private static void Render(SceneSnapshot snapshot)
    {
        Console.Clear();
        Console.WriteLine($"U3D Viewer | snapshot #{snapshot.Sequence} | scenes: {snapshot.Scenes.Length}");
        Console.WriteLine(new string('-', 72));

        foreach (var scene in snapshot.Scenes)
        {
            Console.WriteLine($"▼ Scene: {scene.Name}  [buildIndex={scene.BuildIndex}, loaded={scene.IsLoaded}]");
            foreach (var root in scene.Roots)
            {
                RenderObject(root, 1);
            }
        }
    }

    private static void RenderObject(GameObjectInfo gameObject, int depth)
    {
        var prefix = new string(' ', depth * 2);
        Console.WriteLine($"{prefix}├─ {gameObject.Name}  #{gameObject.InstanceId}");

        foreach (var child in gameObject.Children)
        {
            RenderObject(child, depth + 1);
        }
    }
}
