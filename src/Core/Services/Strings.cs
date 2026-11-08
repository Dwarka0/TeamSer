namespace TeamSpeakManager.Core.Services;

/// <summary>Persian UI strings. Kept as a static table; localisation framework would be overhead.</summary>
public static class Strings
{
    public static string AppTitle => "TeamSer";
    public static string AppTitleShort => "TeamSer";

    public static string Login => "ورود";
    public static string LoginPassword => "رمز عبور دسترسی";
    public static string LoginButton => "ورود به برنامه";
    public static string LoginInvalidPassword => "رمز عبور نامعتبر است.";
    public static string LoginRateLimited => "تعداد تلاش‌های ناموفق زیاد است، بعداً تلاش کنید.";
    public static string LoginNetworkFail => "ارتباط با سرور تأیید ممکن نشد.";

    public static string Dashboard => "داشبورد";
    public static string Settings => "تنظیمات";
    public static string Logs => "لاگ‌ها";
    public static string Network => "شبکه";

    public static string ServerStatus => "وضعیت سرور";
    public static string Online => "آنلاین";
    public static string Offline => "خاموش";
    public static string Starting => "در حال روشن شدن";
    public static string Stopping => "در حال خاموش شدن";
    public static string ErrorState => "خطا";

    public static string Changes => "تغییرات";
    public static string Start => "روشن کردن";
    public static string Stop => "خاموش کردن";
    public static string Restart => "راه‌اندازی دوباره";
    public static string Refresh => "بروزرسانی";
    public static string OpenFolder => "پوشه سرور";
    public static string OpenLogs => "پوشه لاگ";

    public static string ConnectedClients => "کاربران متصل";
    public static string MaxCapacity => "ظرفیت بیشتر";
    public static string ServerAddress => "آدرس سرور";
    public static string VoicePort => "پورت صوتی";
    public static string Uptime => "مدت روشن بودن";
    public static string InstallationStatus => "وضعیت نصب";
    public static string TSInstalled => "نصب شده";
    public static string TSNotInstalled => "نصب نشده";

    public static string PublicIp => "آدرس عمومی";
    public static string LocalIp => "آدرس داخلی";
    public static string CgNatWarning =>
        "این سیستم پشت CGNAT قرار دارد و دستگاه‌های دیگر نمی‌توانند مستقیم وصل شوند؛ هیچ پورت‌فرواردی بر روی آن کار خواهد کرد.";
    public static string CopyAddress => "کپی آدرس اتصال";
    public static string Copied => "کопیه شد";

    public static string ConfirmStop => "سرور خاموش شود؟ کاربران حاضر جدا می‌شوند.";
    public static string ConfirmRestart => "سرور دوباره راه‌اندازی شود؟";
    public static string ConfirmCancel => "رد کردن";
    public static string ConfirmOk => "تأیید";

    public static string ErrorTitle => "خطا";
    public static string InfoTitle => "پیام";
    public static string Close => "بستن";
    public static string Cancel => "لغو";
    public static string Save => "ذخیره";

    public static string SelectServerDirectory => "انتخاب پوشه نصب سرور";
    public static string NotConfigured => "سرور تنظیم نشده";
    public static string StartedSuccessfully => "روشن شد";
    public static string StoppedSuccessfully => "خاموش شد";
    public static string StartFailed => "روشن نشد";
    public static string StopFailed => "خاموش نشد";
    public static string InvalidCredentials => "اعتبارلاگ نادرست است";
    public static string ServerQueryConfigured => "اتصال ServerQuery تنظیم شده";
    public static string NoExternalIp => "آدرس عمومی یافت نشد";
    public static string LocalOnly => "فقط شبکه داخلی";
}
