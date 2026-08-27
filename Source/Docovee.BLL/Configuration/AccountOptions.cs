namespace Docovee.BLL.Configuration;

public class AccountOptions
{
    public const string SectionName = "Account";

    /// <summary>
    /// Days after Close Account before admin can permanently remove the account.
    /// 0 = permanent remove is available immediately after closure.
    /// </summary>
    public int HardDeleteWaitDays { get; set; } = 7;
}
