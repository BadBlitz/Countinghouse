namespace Countinghouse.Domain.Entities;

// Small key/value store for user-set configuration that shouldn't live in a committed config
// file — the backup destination is a path on the user's own network, and this repo is public.
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
