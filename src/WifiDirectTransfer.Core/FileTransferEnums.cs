namespace WifiDirectTransfer.Core;

public enum MessageType : byte
{
    FileStart = 0x01,
    FileChunk = 0x02,
    FileEnd = 0x03,
    ControlMessage = 0x04,
    TextMessage = 0x05,
    ClipboardUpdate = 0x06,
    ScreenCapture = 0x07,
    InputEvent = 0x08
}

public enum ControlMessageSubtype : byte
{
    Ack = 0x01,
    ResumeRequest = 0x02,
    TransferError = 0x03,
    QueueInfo = 0x04,
    FileSkipped = 0x05,
    FileVerified = 0x06,
    Hello = 0x07,
    TextRejected = 0x08,
    DirectoryManifest = 0x09,
    IdentityHello = 0x0A,
    IdentityProof = 0x0B,
    IdentityTrust = 0x0C
}

public enum SameNamePolicy
{
    AutoRename = 0,
    Overwrite = 1,
    Skip = 2
}

public enum ChecksumAlgorithm : byte
{
    None = 0x00,
    Sha256 = 0x01
}

[Flags]
public enum FileFlags : byte
{
    None = 0,
    LastFile = 1 << 0
}
