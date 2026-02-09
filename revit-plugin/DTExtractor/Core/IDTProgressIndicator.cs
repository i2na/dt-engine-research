namespace DTExtractor.Core
{
    public interface IDTProgressIndicator
    {
        void Report(int current, int total);
        void Close();
    }
}
