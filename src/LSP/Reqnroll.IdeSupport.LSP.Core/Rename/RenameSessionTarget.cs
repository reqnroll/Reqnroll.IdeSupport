#nullable enable

namespace Reqnroll.IdeSupport.LSP.Core.Rename;

/// <summary>
/// Which binding a <c>reqnroll/selectRenameTarget</c> disambiguation picked, held until the
/// following <c>textDocument/rename</c> consumes it.
/// </summary>
/// <param name="AttributeIndex">
/// The picker's positional index into the candidate list, as the client reported it.
/// </param>
/// <param name="BindingIdentity">
/// A content-addressed key for the picked binding (see <c>RenameBindingIdentity</c>), or
/// <see langword="null"/> when the server could not derive one — an older client that sends no
/// position with its selection, leaving <paramref name="AttributeIndex"/> as the only thing to go on.
/// </param>
/// <remarks>
/// The index alone is not a safe identifier across the modal "Enter the new step expression"
/// dialog: the rename re-derives the candidate list against current state, so an edit made while
/// the dialog was open can leave index <c>N</c> denoting a different binding (issue #671, R5).
/// The identity is what lets the rename find the same binding again, or determine that it is gone.
/// </remarks>
public sealed record RenameSessionTarget(int AttributeIndex, string? BindingIdentity);
