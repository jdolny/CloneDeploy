using System.Diagnostics;
using System.Runtime.InteropServices;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Reads Windows location reports without prompting or changing location permissions.</summary>
internal static class WindowsLocationCollector
{
    internal static void Collect(InventoryOs os, CancellationToken ct)
    {
        // This is the automation interface to the Windows Location API used by GeoCoordinateWatcher.
        // https://learn.microsoft.com/windows/win32/locationapi/locationdisp-latlongreportfactory
        dynamic factory = Activator.CreateInstance(Type.GetTypeFromProgID("LocationDisp.LatLongReportFactory", true)!)!;
        var listening = false;
        try
        {
            factory.DesiredAccuracy = 1u;
            factory.ListenForReports(0u);
            listening = true;
            var elapsed = Stopwatch.StartNew();
            do
            {
                ct.ThrowIfCancellationRequested();
                var status = (uint)factory.Status;
                if (status is 0 or 2) { os.LocationEnabled = false; return; }
                if (status is 3 or 4) os.LocationEnabled = true;
                if (status == 4)
                {
                    object? reportObject = null;
                    try
                    {
                        reportObject = factory.LatLongReport;
                        if (reportObject is not null)
                        {
                            dynamic report = reportObject;
                            var latitude = (double)report.Latitude;
                            var longitude = (double)report.Longitude;
                            var timestamp = DateTime.SpecifyKind((DateTime)report.Timestamp, DateTimeKind.Utc);
                            if (double.IsFinite(latitude) && latitude is >= -90 and <= 90 &&
                                double.IsFinite(longitude) && longitude is >= -180 and <= 180 && timestamp > DateTime.UnixEpoch)
                            {
                                os.Latitude = latitude;
                                os.Longitude = longitude;
                                os.LastLocationUpdatedAtUtc = timestamp;
                                return;
                            }
                        }
                    }
                    // Windows may report Running before the first position is available.
                    catch (COMException ex) when (ex.HResult == unchecked((int)0x800700E8)) { }
                    finally { if (reportObject is not null) Marshal.FinalReleaseComObject(reportObject); }
                }
                if (ct.WaitHandle.WaitOne(250)) ct.ThrowIfCancellationRequested();
            } while (elapsed.Elapsed < TimeSpan.FromSeconds(10));
            throw new TimeoutException("Windows did not provide a location report within ten seconds.");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            os.LocationEnabled = false;
        }
        finally
        {
            try { if (listening) factory.StopListeningForReports(); }
            finally { Marshal.FinalReleaseComObject(factory); }
        }
    }
}
