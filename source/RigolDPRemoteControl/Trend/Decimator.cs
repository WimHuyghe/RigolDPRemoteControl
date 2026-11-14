using System.Buffers;

namespace RigolDPRemoteControl.Trend;

/// <summary>
/// Turns (possibly millions of) raw samples into a few thousand display points for the visible x range,
/// with optional centred moving-average smoothing. Min/max per bucket keeps spikes visible.
/// </summary>
public static class Decimator
{
    public static int LowerBound(List<double> a, double x)
    {
        int lo = 0, hi = a.Count;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if (a[mid] < x) lo = mid + 1; else hi = mid; }
        return lo;
    }

    public static int UpperBound(List<double> a, double x)
    {
        int lo = 0, hi = a.Count;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if (a[mid] <= x) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>
    /// Fills outX/outY with display data for xs within [x0, x1] (plus one point either side).
    /// Returns the y-range of the points inside [visX0, visX1].
    /// Caller must hold the series lock.
    /// </summary>
    public static (double min, double max) Build(List<double> xs, List<double> ys, double x0, double x1,
        double visX0, double visX1, int buckets, int smoothN, List<double> outX, List<double> outY)
    {
        outX.Clear(); outY.Clear();
        double ymin = double.PositiveInfinity, ymax = double.NegativeInfinity;
        int count = xs.Count;
        if (count == 0) return (ymin, ymax);

        int i0 = Math.Max(0, LowerBound(xs, x0) - 1);
        int i1 = Math.Min(count, UpperBound(xs, x1) + 1); // exclusive
        int n = i1 - i0;
        if (n <= 0) return (ymin, ymax);

        double[]? rented = null;
        Func<int, double> y;
        if (smoothN > 1)
        {
            // centred moving average computed with a running sum (window shrinks at the edges)
            rented = ArrayPool<double>.Shared.Rent(n);
            int h = smoothN / 2, h2 = smoothN - 1 - h;
            int a = Math.Max(0, i0 - h), b = Math.Min(count - 1, i0 + h2);
            double sum = 0; int cnt = 0;
            for (int k = a; k <= b; k++) { sum += ys[k]; cnt++; }
            for (int k = i0; k < i1; k++)
            {
                rented[k - i0] = cnt > 0 ? sum / cnt : ys[k];
                // slide window from [k-h, k+h2] to [k+1-h, k+1+h2]
                int remove = k - h, add = k + 1 + h2;
                if (remove >= 0 && remove >= a) { sum -= ys[remove]; cnt--; }
                if (add < count) { sum += ys[add]; cnt++; }
            }
            var arr = rented;
            y = k => arr[k - i0];
        }
        else y = k => ys[k];

        try
        {
            if (buckets < 10 || n <= buckets * 2)
            {
                for (int k = i0; k < i1; k++)
                {
                    double yy = y(k), xx = xs[k];
                    outX.Add(xx); outY.Add(yy);
                    if (xx >= visX0 && xx <= visX1 && !double.IsNaN(yy)) { if (yy < ymin) ymin = yy; if (yy > ymax) ymax = yy; }
                }
            }
            else
            {
                double per = (double)n / buckets;
                for (int bkt = 0; bkt < buckets; bkt++)
                {
                    int s = i0 + (int)(bkt * per);
                    int e = Math.Min(i1, i0 + (int)((bkt + 1) * per));
                    if (e <= s) continue;
                    int iMin = s, iMax = s;
                    double vMin = y(s), vMax = vMin;
                    for (int k = s + 1; k < e; k++)
                    {
                        double v = y(k);
                        if (v < vMin) { vMin = v; iMin = k; }
                        if (v > vMax) { vMax = v; iMax = k; }
                    }
                    if (iMin <= iMax) { outX.Add(xs[iMin]); outY.Add(vMin); if (iMax != iMin) { outX.Add(xs[iMax]); outY.Add(vMax); } }
                    else { outX.Add(xs[iMax]); outY.Add(vMax); outX.Add(xs[iMin]); outY.Add(vMin); }
                    double xs0 = xs[s], xs1 = xs[e - 1];
                    if (xs1 >= visX0 && xs0 <= visX1)
                    {
                        if (vMin < ymin) ymin = vMin;
                        if (vMax > ymax) ymax = vMax;
                    }
                }
            }
        }
        finally
        {
            if (rented != null) ArrayPool<double>.Shared.Return(rented);
        }
        return (ymin, ymax);
    }
}
