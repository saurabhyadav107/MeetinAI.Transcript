using System;
using System.Collections.Generic;
using System.Linq;

public static partial class Enumerable
{
    public static IEnumerable<TSource []> Chunk<TSource> ( this IEnumerable<TSource> source, int size )
    {
        if (source == null)
        {
            throw new ArgumentNullException ("source");
        }

        if (size < 1)
        {
            throw new ArgumentOutOfRangeException ("size");
        }

        return ChunkIterator (source, size);
    }

    private static IEnumerable<TSource []> ChunkIterator<TSource> ( IEnumerable<TSource> source, int size )
    {
        using (var e = source.GetEnumerator ())
        {
            if (e.MoveNext ())
            {
                List<TSource> chunkBuilder = new List<TSource> ();

                while (true)
                {
                    do
                    {
                        chunkBuilder.Add (e.Current);
                    } while (chunkBuilder.Count < size && e.MoveNext ());

                    yield return chunkBuilder.ToArray ();

                    if (chunkBuilder.Count < size || !e.MoveNext ())
                    {
                        yield break;
                    }

                    chunkBuilder.Clear ();
                }
            }
        }
    }
}
