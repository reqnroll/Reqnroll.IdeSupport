using Reqnroll.IdeSupport.Common.Configuration.Model;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

internal record ConfigCache(IdeSupportConfiguration Configuration, ConfigSource[] ConfigSources);
