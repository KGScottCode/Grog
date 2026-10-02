// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later

namespace Grog.Core.Volumes;

/// <summary>How a single file is moved. A same-drive move is an atomic OS rename (instant, no bytes
/// travel, nothing to verify). A cross-drive move copies bytes, verifies the copy, then deletes the
/// source -- never delete-first.</summary>
public enum ReorgMoveKind
{
    Rename = 0,             // same volume: File.Move, atomic
    CopyVerifyDelete = 1,   // different volume: copy -> verify -> delete source
}

/// <summary>Per-file progress through the move. Every transition is journaled, so an interrupted job
/// always stops between fully-consistent states (file + manifest agree) and can resume.</summary>
public enum ReorgMoveState
{
    Pending = 0,    // not started; file + manifest both still at the old path
    Copied = 1,     // cross-drive only: bytes copied to a .part, not yet verified
    Verified = 2,   // cross-drive only: copy verified + promoted to final; source not yet deleted
    Done = 3,       // moved + manifest repathed; source gone (cross-drive) or renamed (same-drive)
    Skipped = 4,    // source not on disk; manifest repathed so future writes land correctly
    Failed = 5,     // verification or IO error; source untouched
}

/// <summary>One file to move. Carries manifest identity (to repath) and both absolute paths.</summary>
public sealed class ReorgMove
{
    public long GogId { get; set; }
    public string FileKey { get; set; } = "";        // stable file identity within the game
    public string Title { get; set; } = "";           // display only
    public string FromRel { get; set; } = "";
    public string ToRel { get; set; } = "";
    public string? ToRootId { get; set; }             // null = stays on same root (rename)
    public string FromAbs { get; set; } = "";
    public string ToAbs { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>True when SizeBytes is GOG's rounded label (the record had no LocalSizeBytes): a size verify
    /// then compares to the source's real length, never to the label.</summary>
    public bool SizeIsLabel { get; set; }
    public string? ExpectedMd5 { get; set; }          // when known, the copy is hash-verified; else size-verified
    public ReorgMoveKind Kind { get; set; }
    public ReorgMoveState State { get; set; } = ReorgMoveState.Pending;
    public string? Error { get; set; }

    /// <summary>Bytes already copied to the destination .part while a cross-drive move is incomplete
    /// (mirrors GameFile.PartialBytes). 0 for same-drive renames and once settled.</summary>
    public long CopiedBytes { get; set; }

    /// <summary>The root the file was on before the move -- what a revert needs to restore RootId after a
    /// cross-root Relocate. Null on same-root jobs; revert then restores the path only.</summary>
    public string? FromRootId { get; set; }

    public bool IsSettled => State is ReorgMoveState.Done or ReorgMoveState.Skipped;
}
