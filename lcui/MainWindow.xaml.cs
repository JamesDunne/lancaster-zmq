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

        private bool IsClientRunning { get; set; }

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

        private void btnClientFolder_Click(object sender, RoutedEventArgs e)
        {
            //System.Windows.Forms.OpenFileDialog;
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
            Dispatcher.BeginInvoke((Action)(() => {
                BitArray tmp = client.NAKs;
                this.pbClientProgress.UpdateProgress(tmp, chunkIdx);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        #endregion

        #region Server logic

        private bool IsServerRunning { get; set; }

        private ServerHost server;
        private Thread serverThread;
        private int serverCurrentIndex;

        private void btnServerStart_Click(object sender, RoutedEventArgs e)
        {
            if (IsServerRunning) return;

            server = new ServerHost(ZMQ.Transport.TCP, "*:12198", String.Empty, new TarballStreamWriter(Enumerable.Empty<FileInfo>()), txtServerFolder.Text, 512000, 50, false);
            server.ClientJoined += new Action<ServerHost, Guid>(server_ClientJoined);
            server.ClientLeft += new Action<ServerHost, Guid, ServerHost.ClientLeaveReason>(server_ClientLeft);
            server.ChunkSent += new Action<ServerHost, int>(server_ChunkSent);
            server.ChunksACKed += new Action<ServerHost>(server_ChunksACKed);

            serverThread = new Thread(new ParameterizedThreadStart(server.Run));
            serverThread.Start();
            IsServerRunning = true;
        }

        void server_ChunksACKed(ServerHost host)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                UpdateServerProgress(host);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        void server_ChunkSent(ServerHost host, int chunkIdx)
        {
            serverCurrentIndex = chunkIdx;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                UpdateServerProgress(host);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        void server_ClientLeft(ServerHost host, Guid clientIdentity, ServerHost.ClientLeaveReason reason)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                this.lblServerClients.Text = host.Clients.Count.ToString();
                // TODO: add an event to a log window
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        void server_ClientJoined(ServerHost host, Guid clientIdentity)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                this.lblServerClients.Text = host.Clients.Count.ToString();
                // TODO: add an event to a log window
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void btnServerFolder_Click(object sender, RoutedEventArgs e)
        {

        }

        #endregion

        #region UI Thread logic

        private void UpdateClientProgress()
        {

        }

        private void UpdateServerProgress(ServerHost host)
        {
            // TODO: update the progress bar control to accept a List<int> instead of BitArray and render it as a heat-map.
            BitArray curr = new BitArray(host.NumBitArrayBytes * 8, false);
            foreach (var cli in host.Clients)
            {
                if (!cli.HasNAKs) continue;
                if (cli.IsTimedOut) continue;
                curr = curr.Or(cli.NAK);
            }
            // Update the server progress bar:
            this.pbServerProgress.UpdateProgress(curr, serverCurrentIndex);
        }

        #endregion
    }
}
