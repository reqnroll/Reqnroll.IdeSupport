global using AwesomeAssertions;
global using Microsoft.CodeAnalysis;
global using Microsoft.CodeAnalysis.CSharp;
global using Microsoft.VisualStudio.Text;
global using Microsoft.VisualStudio.Text.Editor;
global using Microsoft.VisualStudio.Text.Formatting;
global using Microsoft.VisualStudio.Text.Projection;
global using Microsoft.VisualStudio.Text.Tagging;
global using Microsoft.VisualStudio.TextManager.Interop;
global using Microsoft.VisualStudio.Threading;
global using Microsoft.VisualStudio.Utilities;
global using NSubstitute;
// New project namespaces (replacing old Reqnroll.VisualStudio.* equivalents)
global using Reqnroll.IdeSupport.Common;
global using Reqnroll.IdeSupport.Common.Telemetry;
global using Reqnroll.IdeSupport.Common.Configuration;
global using Reqnroll.IdeSupport.Common.Logging;
global using Reqnroll.IdeSupport.Common.ProjectSystem;
global using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
global using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
global using Reqnroll.IdeSupport.LSP.Core.Bindings;
global using Reqnroll.IdeSupport.LSP.Core.Documents;



global using Reqnroll.IdeSupport.VisualStudio;
// BCL
global using System;
global using System.Collections;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Collections.Immutable;
global using System.Diagnostics;
global using System.IO;
global using System.IO.Abstractions;
global using System.IO.Abstractions.TestingHelpers;
global using System.Linq;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Text.RegularExpressions;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Windows;
global using System.Windows.Media;
global using Xunit.Abstractions;
