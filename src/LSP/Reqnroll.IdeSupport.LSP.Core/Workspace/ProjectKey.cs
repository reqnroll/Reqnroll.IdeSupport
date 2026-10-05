namespace Reqnroll.IdeSupport.LSP.Core.Workspace;

/// <summary>
/// Identifies a (project, TFM) combination in the membership index.
/// Phase 1 matches on <see cref="ProjectFile"/> only; <see cref="Tfm"/> is stored for a
/// future per-TFM keying follow-up.
/// </summary>
public readonly record struct ProjectKey(string ProjectFile, string Tfm);
