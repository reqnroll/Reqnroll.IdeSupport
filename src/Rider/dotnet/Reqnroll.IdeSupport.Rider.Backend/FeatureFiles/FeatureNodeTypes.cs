using JetBrains.ReSharper.Psi;
using System;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Tree;
using JetBrains.ReSharper.Psi.TreeBuilder;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

[Language(typeof(FeatureLanguage))]
public class FeatureNodeTypes : INodeTypesInitializer
{
    public static readonly CompositeNodeWithArgumentType File = new FileNodeType();

    private sealed class FileNodeType : CompositeNodeWithArgumentType
    {
        public FileNodeType() : base("REQNROLL_FEATURE_FILE", 5201, typeof(FeatureFile))
        {
        }

        public override CompositeElement Create(object userData) => new FeatureFile();

        public override CompositeElement Create() => Create(null);
    }
}
