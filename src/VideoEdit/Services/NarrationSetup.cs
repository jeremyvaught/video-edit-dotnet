using ElevenLabs;

namespace VideoEdit.Services;

public static class NarrationSetup
{
    public static void AddNarration(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(_ => new ElevenLabsClient(
            new ElevenLabsAuthentication(LoadApiKey(builder.Configuration, builder.Environment.ContentRootPath))));
        builder.Services.AddSingleton<INarrationProvider, ElevenLabsNarrationProvider>();
        builder.Services.AddSingleton<NarrationService>();
        builder.Services.AddSingleton<NarrationMixService>();
    }

    public static string LoadApiKey(IConfiguration configuration, string contentRoot)
    {
        var key = configuration["ELEVENLABS_API_KEY"];
        if (!string.IsNullOrWhiteSpace(key)) return key;

        // dotnet run starts in src/VideoEdit. Stop at this repository rather than
        // accidentally loading another project's or the user's home env file.
        var directory = new DirectoryInfo(contentRoot);
        while (directory.Parent is not null && !File.Exists(Path.Combine(directory.FullName, "VideoEdit.slnx")))
            directory = directory.Parent;
        var root = File.Exists(Path.Combine(directory.FullName, "VideoEdit.slnx")) ? directory.FullName : contentRoot;
        var envPath = configuration["ElevenLabs:EnvFile"] ?? Path.Combine(root, ".env.local");
        try
        {
            if (File.Exists(envPath))
                key = DotNetEnv.Env.NoEnvVars().Load(envPath)
                    .LastOrDefault(pair => pair.Key == "ELEVENLABS_API_KEY").Value;
        }
        catch (Exception)
        {
            throw VideoEditException.Validation("Could not load the ElevenLabs env file. Run the onboarding key check; do not paste the file into logs or chat.");
        }

        return !string.IsNullOrWhiteSpace(key) ? key : throw VideoEditException.Validation(
            "ELEVENLABS_API_KEY is missing. Run ElevenLabs onboarding or set it in the process environment.");
    }
}
