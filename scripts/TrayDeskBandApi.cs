// Tiny helper for the install/uninstall scripts: the shell's ITrayDeskBand, wrapped so the
// PowerShell side never has to do a COM interface cast itself (that casts fails from PS 5.1).
using System;
using System.Runtime.InteropServices;

namespace QuadVitals.Install
{
    [ComImport, Guid("6D67E846-5B9C-4db8-9CBC-DDE12F4254F1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITrayDeskband
    {
        [PreserveSig] int ShowDeskBand([In, MarshalAs(UnmanagedType.Struct)] ref Guid clsid);
        [PreserveSig] int HideDeskBand([In, MarshalAs(UnmanagedType.Struct)] ref Guid clsid);
        [PreserveSig] int IsDeskBandShown([In, MarshalAs(UnmanagedType.Struct)] ref Guid clsid);
        [PreserveSig] int DeskBandRegistrationChanged();
    }

    public static class TrayDeskBandApi
    {
        static ITrayDeskband Create()
        {
            Type type = Type.GetTypeFromCLSID(new Guid("E6442437-6C68-4f52-94DD-2CFED267EFB9"));
            return (ITrayDeskband)Activator.CreateInstance(type);
        }

        public static int Refresh()
        {
            ITrayDeskband api = Create();
            try { return api.DeskBandRegistrationChanged(); }
            finally { Marshal.ReleaseComObject(api); }
        }

        // No Show/ShowDeskBand wrapper on purpose: see the note in install-deskband.ps1. Explorer answers a
        // show request with its own dialog, and gating that dialog is a race. The band appears by the user
        // ticking the toolbar entry, which is the only path that does not ask us to answer for them.
        public static int Hide(string clsid)
        {
            Guid guid = new Guid(clsid);
            ITrayDeskband api = Create();
            try { return api.HideDeskBand(ref guid); }
            finally { Marshal.ReleaseComObject(api); }
        }

        // IsShown/Show stay out of this wrapper on purpose: nothing in the product needs to ask the shell to
        // show the band (that is what made Explorer raise two of its own dialogs), and asking whether it is
        // shown only existed so the installer could decide to show it. Both calls are gone with that logic.
    }
}
