using Reqnroll;

// Bindings whose exact method identity the discovery specs check: two binding classes that share
// a short name in different namespaces (the wire method reference alone cannot tell them apart),
// overloads of one method, and a step whose parameter is a closed generic type produced by a
// step argument transformation. They share one file, so locating each method in the source by
// its name alone would anchor all of them on the first declaration.
namespace ReqnrollBindingsFixture.Identity
{
    public class Book
    {
        public string Title { get; set; }
    }

    [Binding]
    public class BookTransformations
    {
        [StepArgumentTransformation]
        public List<Book> ToBooks(DataTable table) =>
            table.Rows.Select(row => new Book { Title = row["Title"] }).ToList();
    }
}

namespace ReqnrollBindingsFixture.Identity.First
{
    [Binding]
    public class SharedNameSteps
    {
        [Given("a step on the first same-named class")]
        public void GivenAStep() { }

        [Given("a step on the first same-named class with number {int}")]
        public void GivenAStep(int number) { _ = number; }

        [Given("these books")]
        public void GivenTheseBooks(List<Book> books) { _ = books; }
    }
}

namespace ReqnrollBindingsFixture.Identity.Second
{
    [Binding]
    public class SharedNameSteps
    {
        [Given("a step on the second same-named class")]
        public void GivenAStep() { }
    }
}
