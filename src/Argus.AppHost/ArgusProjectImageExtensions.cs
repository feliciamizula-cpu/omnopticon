using Aspire.Hosting.ApplicationModel;

internal static class ArgusProjectImageExtensions
{
    public static IResourceBuilder<ProjectResource> PublishAsArgusImage(
        this IResourceBuilder<ProjectResource> project,
        string projectPath,
        string assemblyName)
    {
        return project.PublishAsDockerFile(container =>
        {
            container
                .WithDockerfile("../..", "deploy/Dockerfile.service")
                .WithBuildArg("PROJECT_PATH", projectPath)
                .WithBuildArg("ASSEMBLY_NAME", assemblyName);
        });
    }
}
