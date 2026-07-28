global using ApprovalTests.Namers;
global using ApprovalTests.Reporters;
global using ApprovalTests;
global using AwesomeAssertions;
global using Gherkin.Ast;
global using Microsoft.CodeAnalysis.CSharp;
global using Microsoft.CodeAnalysis;
global using Microsoft.VisualStudio.Text;
global using Microsoft.VisualStudio.Text.Editor;
global using Microsoft.VisualStudio.Text.Tagging;
global using Microsoft.VisualStudio.TextManager.Interop;
global using Microsoft.VisualStudio.Utilities;
global using NSubstitute;
// New Reqnroll.IdeSupport.* namespaces (replacing old Reqnroll.VisualStudio.*)
global using Reqnroll.IdeSupport.Common;
global using Reqnroll.IdeSupport.Common.Telemetry;
global using Reqnroll.IdeSupport.Common.Configuration;
global using Reqnroll.IdeSupport.Common.Logging;
global using Reqnroll.IdeSupport.Common.ProjectSystem;
global using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
global using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
global using Reqnroll.IdeSupport.LSP.Core.Bindings;
global using Reqnroll.IdeSupport.LSP.Core.TagExpressions;


global using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;


global using Reqnroll.IdeSupport.LSP.Connector.Models;
// Reqnroll.IdeSupport.VisualStudio.Telemetry excluded from global using to avoid ambiguity with Common.Telemetry
// Reqnroll.IdeSupport.VisualStudio.Logging excluded from global using to avoid ambiguity with Common.Logging
global using Reqnroll.IdeSupport.VisualStudio;
global using Reqnroll.IdeSupport.VisualStudio.ProjectSystem;
// Stubs project namespaces
global using Reqnroll.VisualStudio.VsxStubs;
global using Reqnroll.VisualStudio.VsxStubs.ProjectSystem;
// Old interface alias: IFileSystemForVs -> IFileSystemForIDE
global using IFileSystemForVs = Reqnroll.IdeSupport.Common.IFileSystemForIDE;
// Deferred: Reqnroll.VisualStudio.VsxStubs.StepDefinitions (MockableDiscoveryService not yet ported)
// Deferred: Reqnroll.VisualStudio.UI.ViewModels (UI not yet ported)
// Deferred: Reqnroll.VisualStudio.Editor.* (editor commands/services not yet ported)
// BCL
global using System;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Collections.Immutable;
global using System.Diagnostics;
global using System.IO.Abstractions;
global using System.IO.Abstractions.TestingHelpers;
global using System.Linq;
global using System.Reflection;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Text.RegularExpressions;
global using System.Threading;
global using System.Threading.Tasks;
global using Xunit.Abstractions;
global using Xunit;
