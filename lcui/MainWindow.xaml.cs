using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Threading;
using System.IO;
using System.Collections;

namespace WellDunne.LanCaster.GUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        #region Client logic

        private bool IsClientRunning
        {
            get;
            set;
        }

        private ClientHost client;
        private Thread clientThread;

        private void WaitForClient()
        {
            clientThread.Join();
        }

        /// <summary>
        /// Connect to server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnClientConnect_Click(object sender, RoutedEventArgs e)
        {
            if (IsClientRunning)
            {
                MessageBox.Show(this, @"Client is already connected!", "Connect", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (String.IsNullOrEmpty(txtClientFolder.Text.Trim()))
            {
                MessageBox.Show(this, @"Please select a download location first.", "Connect", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (String.IsNullOrEmpty(txtClientConnectIP.Text.Trim()))
            {
                MessageBox.Show(this, @"Please enter a server address first.", "Connect", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Create the download folder if it doesn't exist:
            DirectoryInfo downloadFolder = new DirectoryInfo(txtClientFolder.Text.Trim());
            if (!downloadFolder.Exists)
            {
                downloadFolder.Create();
            }

            client = new ClientHost(ZMQ.Transport.TCP, txtClientConnectIP.Text.Trim(), String.Empty, downloadFolder, false, new ClientHost.GetClientNAKStateDelegate(GetClientNAKState), 64);
            client.ChunkWritten += new Action<ClientHost, int>(client_ChunkWritten);

            // Create a new thread to run the client on and start it:
            clientThread = new Thread(new ParameterizedThreadStart(client.Run));
            clientThread.Start();
            IsClientRunning = true;
        }

        BitArray GetClientNAKState(ClientHost host, TarballStreamReader tarball)
        {
            // TODO: Copy over code from LCC to maintain resumable download state.
            return new BitArray(host.NumBytes).Not();
        }

        /// <summary>
        /// Event fired from client thread when a chunk is written to disk.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="chunkIdx"></param>
        void client_ChunkWritten(ClientHost host, int chunkIdx)
        {
            // BeginInvoke to the UI thread.
        }

        #endregion

    }
}
