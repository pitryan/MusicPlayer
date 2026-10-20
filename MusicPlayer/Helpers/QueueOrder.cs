using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicPlayer.Helpers;

/// <summary>Logika mengacak dan mengembalikan urutan antrean. Sengaja tanpa kode tampilan, supaya mudah diuji.</summary>
public static class QueueOrder
{
    /// <summary>Mengacak urutan daftar (algoritma Fisher–Yates: setiap urutan punya peluang yang sama).</summary>
    public static List<T> Shuffled<T>(IEnumerable<T> items, Random? random = null)
    {
        random ??= Random.Shared;
        var list = items.ToList();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    /// <summary>
    /// Mengembalikan urutan asli setelah shuffle dimatikan. Item yang masih ada kembali ke urutan aslinya.
    /// Item yang ditambahkan selama shuffle aktif tetap di posisinya, jadi lagu dari "Play Next" tetap
    /// di paling atas dan lagu dari "Add to Queue" tetap di paling bawah.
    /// </summary>
    public static List<T> Restored<T>(IReadOnlyList<T> current, IReadOnlyList<T> original) where T : class
    {
        var present = new HashSet<T>(current, ReferenceEqualityComparer.Instance);
        var fromOriginal = new HashSet<T>(original, ReferenceEqualityComparer.Instance);

        var restored = original.Where(present.Contains).ToList();
        for (int i = 0; i < current.Count; i++)
        {
            if (!fromOriginal.Contains(current[i]))
                restored.Insert(Math.Min(i, restored.Count), current[i]);
        }
        return restored;
    }
}
