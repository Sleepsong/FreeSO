using System.Collections;
using System.Collections.Generic;

namespace FSO.LotView.Model
{
    /// <summary>
    /// Something kept per level of a lot (walls, floors, rooms...), indexed as the plain per-level arrays always were: by
    /// level - 1, so [0] is the ground floor. It also has the basement, level 0, at [-1]: code that looks things up by
    /// level - 1 reaches the basement unchanged. Length, and enumerating, still cover the ground floor and up (as many
    /// as before); code that goes over every level and should include the basement starts at <see cref="MinIndex"/>.
    /// </summary>
    public class LevelArray<T> : IEnumerable<T>
    {
        /// <summary>The lowest index: -1, the basement (level 0).</summary>
        public const int MinIndex = -1;

        private T[] Items; //[0] is the basement, [1] the ground floor...

        /// <summary>Levels 0 (the basement) to <paramref name="stories"/>.</summary>
        public LevelArray(int stories)
        {
            Items = new T[stories + 1];
        }

        /// <summary>By level - 1: [-1] is the basement, [0] the ground floor.</summary>
        public T this[int index]
        {
            get { return Items[index + 1]; }
            set { Items[index + 1] = value; }
        }

        /// <summary>How many levels from the ground floor up (the length the per-level arrays had).</summary>
        public int Length => Items.Length - 1;

        /// <summary>The basement, level 0 (index -1).</summary>
        public T Basement
        {
            get { return Items[0]; }
            set { Items[0] = value; }
        }

        /// <summary>From the ground floor and up (a plain per-level array), and the basement.</summary>
        public static LevelArray<T> From(T[] levels, T basement)
        {
            var result = new LevelArray<T>(levels.Length);
            for (int i = 0; i < levels.Length; i++) result[i] = levels[i];
            result.Basement = basement;
            return result;
        }

        /// <summary>The ground floor and up, as a plain per-level array (without the basement).</summary>
        public T[] ToArray()
        {
            var result = new T[Length];
            for (int i = 0; i < result.Length; i++) result[i] = this[i];
            return result;
        }

        /// <summary>The ground floor and up, as the per-level arrays enumerated.</summary>
        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 1; i < Items.Length; i++) yield return Items[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
