namespace MusicPlayer.Library.Tests;

public sealed class QueueOrderTests
{
    [Fact]
    public void Shuffled_KeepsEveryItemOnce()
    {
        int[] items = Enumerable.Range(1, 50).ToArray();

        var shuffled = QueueOrder.Shuffled(items, new Random(7));

        Assert.NotEqual(items, shuffled);
        Assert.Equal(items, shuffled.Order());
    }

    [Fact]
    public void Shuffled_SameSeed_GivesTheSameOrder() =>
        Assert.Equal(QueueOrder.Shuffled(Enumerable.Range(1, 20), new Random(3)), QueueOrder.Shuffled(Enumerable.Range(1, 20), new Random(3)));

    [Fact]
    public void Restored_PutsRemainingItemsBackInTheirOriginalOrder()
    {
        var (a, b, c, d) = (new Item("A"), new Item("B"), new Item("C"), new Item("D"));

        // C sudah diputar (keluar dari antrean) selama shuffle aktif
        var restored = QueueOrder.Restored([d, a, b], [a, b, c, d]);

        Assert.Equal([a, b, d], restored);
    }

    [Fact]
    public void Restored_ItemsAddedWhileShuffled_KeepTheirPlace()
    {
        var (a, b, c) = (new Item("A"), new Item("B"), new Item("C"));
        var playNext = new Item("Play Next");
        var added = new Item("Add to Queue");

        var restored = QueueOrder.Restored([playNext, c, a, b, added], [a, b, c]);

        Assert.Equal([playNext, a, b, c, added], restored);
    }

    [Fact]
    public void Restored_SameSongTwice_IsTreatedAsTwoItems()
    {
        // Lagu yang sama dua kali di antrean: dua baris berbeda dengan isi yang sama
        var (first, second) = (new Item("A"), new Item("A"));

        var restored = QueueOrder.Restored([second, first], [first, second]);

        Assert.Same(first, restored[0]);
        Assert.Same(second, restored[1]);
    }

    private sealed class Item(string name)
    {
        public override string ToString() => name;
    }
}
