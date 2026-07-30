namespace RAWimp.App;

// An MTP device (camera/phone) serves ONE transfer at a time. Every read in the app — grid
// thumbnails, big previews, imports — has to queue behind the same lock; two independent locks meant
// the thumbnail loader and the importer fought each other and every read failed with
// ERROR_BUSY (0x800700AA).
static class Mtp
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
