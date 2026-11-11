namespace Note
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            Routing.RegisterRoute("edit", typeof(NoteEditPage));
            Routing.RegisterRoute("viewer", typeof(DocumentPage));
            Routing.RegisterRoute("browser", typeof(FileBrowserPage));
        }
    }
}
