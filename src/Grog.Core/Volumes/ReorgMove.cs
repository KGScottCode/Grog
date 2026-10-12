// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later

namespace Grog.Core.Volumes;

public enum ReorgMoveKind
{
    Rename = 0,             // same volume: File.Move, atomic
    CopyVerifyDelete = 1,   // different volume: copy -> verify -> delete source
}

public enum ReorgMoveState
{
    Pending = 0,    // not started; file + manifest both still at the old path
    Copied = 1,     // cross-drive only: bytes copied to a .part, not yet verified
    Verified = 2,   // cross-drive only: copy verified + promoted to final; source not yet deleted
    Done = 3,       // moved + manifest repathed; source gone (cross-drive) or renamed (same-drive)
    Skipped = 4,    // source not on disk; manifest repathed so future writes land correctly
    Failed = 5,     // verification or IO error; source untouched
}

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
    public bool SizeIsLabel { get; set; }
    public string? ExpectedMd5 { get; set; }          // when known, the copy is hash-verified; else size-verified
    public ReorgMoveKind Kind { get; set; }
    public ReorgMoveState State { get; set; } = ReorgMoveState.Pending;
    public string? Error { get; set; }

    public long CopiedBytes { get; set; }

    public string? FromRootId { get; set; }

    public bool IsSettled => State is ReorgMoveState.Done or ReorgMoveState.Skipped;
}
