using Reqnroll.IdeSupport.Common.Configuration;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

internal record ConfigCache(IdeSupportConfiguration Configuration, ConfigSource[] ConfigSources);
