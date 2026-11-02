namespace MirbadLLM.Web;

/// <summary>The info (ⓘ) texts of the Backup page's Destination panel, in one place so the wording is reviewable.</summary>
public static class BackupFieldHelp
{
    public static readonly FieldHelpText Kind = new(
        "Where scheduled backups are written: this DGX's own disk, or a shared folder on the NAS.",
        "Backups on the DGX's disk are lost if the DGX is reinstalled. A NAS share is mounted on the DGX itself at /srv/mirbadllm/backups, so the files survive a reinstall and you restore from them afterwards.",
        "SMB share \"backups\" on 192.0.2.200");

    public static readonly FieldHelpText Server = new(
        "The NAS's IP address or host name.",
        "The DGX connects to it directly. An IP address avoids depending on DNS when the DGX boots.",
        "192.0.2.200");

    public static readonly FieldHelpText SmbShare = new(
        "The shared folder's name on the NAS, without slashes.",
        "On a Synology the shared folders are listed under Control Panel → Shared Folder; the user below needs read/write access to this one.",
        "backups");

    public static readonly FieldHelpText NfsExport = new(
        "The exported path on the NAS.",
        "On a Synology it is /volume1/<shared folder>. The export's NFS permissions must allow this DGX's address (Control Panel → Shared Folder → Edit → NFS Permissions), and the folder must be writable by uid 1654, the user mirbadllm runs as.",
        "/volume1/backups");

    public static readonly FieldHelpText Subfolder = new(
        "An optional folder inside the share. It must already exist on the NAS.",
        "Keeps MirbadLLM's files apart from other backups on the same share. Only mirbadllm-backup-….json files are ever listed or pruned.",
        "mirbadllm");

    public static readonly FieldHelpText Username = new(
        "The NAS user the backup files are written as.",
        "Give MirbadLLM its own NAS account with write access to this one share, so the DGX cannot touch anything else on the NAS.",
        "dgx-backup");

    public static readonly FieldHelpText Password = new(
        "The NAS user's password.",
        "Sent once to the host agent on the DGX, which keeps it in a root-only file (/etc/mirbadllm-hostd/backup-share.cred). MirbadLLM never stores it, shows it again, logs it or puts it in a backup — enter it again whenever you save.",
        "The NAS account's password");

    public static readonly FieldHelpText Domain = new(
        "The Windows or Active Directory domain of the user, if it has one.",
        "Leave it empty for a local NAS user, the usual case on a Synology.",
        "WORKGROUP");

    public static readonly FieldHelpText SmbVersion = new(
        "The SMB protocol version the DGX speaks to the NAS.",
        "3.0 works with Synology DSM 6/7 and most current NAS. 3.1.1 adds stronger integrity checks; 2.1 is only for old devices. If the test says the version is not supported, try another.",
        "3.0");

    public static readonly FieldHelpText NfsVersion = new(
        "The NFS protocol version.",
        "4.1 needs only port 2049 and is the default. Choose 3 if the NAS only offers NFSv3.",
        "4.1");
}
