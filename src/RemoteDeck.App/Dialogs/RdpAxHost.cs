using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteDeck.App.Dialogs;

/// <summary>Hosts the Windows Remote Desktop ActiveX control (mstscax.dll) that ships with Windows.</summary>
internal sealed class RdpAxHost : AxHost
{
    public RdpAxHost(string clsid)
        : base(clsid)
    {
    }

    /// <summary>The newest Remote Desktop control registered on this computer, or null when none is.</summary>
    public static string? FindClsid()
    {
        // Newest first: MsRdpClient9 (Windows 8.1 and later) down to MsRdpClient3.
        string[] candidates =
        {
            "8B918B82-7985-4C24-89DF-C33AD2BBFBCD",
            "54d38bf7-b1ef-4479-9674-1bd6ea465258",
            "d2ea46a7-c2bf-426b-af24-e19c44456399",
            "4eb2f086-c818-447e-b32c-c51ce2b30d31",
            "6ae29350-321b-42be-bbe5-12fb5270c0de",
            "ace575fd-1fcf-4074-9401-ebab990fa9de",
        };

        foreach (var clsid in candidates)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"CLSID\\{{{clsid}}}\\InprocServer32");
                if (key is not null)
                {
                    return clsid;
                }
            }
            catch (Exception)
            {
                // Try the next one.
            }
        }

        return null;
    }
}

/// <summary>
/// The part of the control that takes the password. It is not scriptable (not on IDispatch), so it is reached through
/// its COM interface directly. Only the first method is declared; the rest of the table is never called.
/// </summary>
[ComImport]
[Guid("c1e6743a-41c1-4a74-832a-0dd06c1c7a0e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    void put_ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string password);
}
