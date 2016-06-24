using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using ProtoBuffer;

namespace hyperionRemote
{
  class HyperionHandler
  {
    #region Fields

    // Network settings
    private static TcpClient _socket = new TcpClient();
    private Stream _stream;
    public static string hyperionIP = "10.0.0.1";
    public static int hyperionPort = 19445;
    private string hyperionpreviousHostname = "";
    public static int hyperionPriority = 10;
    public static int hyperionPriorityStaticColor = 50;

    // Capture settings
    public static int captureWidth = 64;
    public static int captureHeight = 64;

    // State settings
    private bool isInit;
    private volatile bool initLock;
    private bool priorityCleared;
    private readonly bool reInitOnError = true;

    // Reconnect settings
    private int hyperionReconnectCounter;
    public int hyperionReconnectAttempts = 20;
    public int hyperionReconnectDelay = 1000;
    private readonly Stopwatch liveReconnectSW = new Stopwatch();
    private bool liveReconnectEnabled;

    #endregion

    #region Hyperion

    public void Initialise(bool force = false)
    {
      try
      {
        if (!initLock)
        {
         // Log.Debug("HyperionHandler - Initialising");
          hyperionReconnectCounter = 0;
          initLock = true;
          isInit = true;

          // Check if Hyperion is IP or hostname and resolve
          HyperionHostnameCheckResolve();

          if (liveReconnectEnabled)
          {
            liveReconnect();
          }
          else
          {
            Connect();
          }
        }
        else
        {
         // Log.Debug("HyperionHandler - Initialising locked.");
        }
      }
      catch (Exception e)
      {
        // Log.Error("HyperionHandler - Error during initialise");
        // Log.Error("HyperionHandler - Exception: {0}", e.Message);
      }
    }

    public void ReInitialise(bool force = false)
    {
      Thread t = new Thread(() => ReInitialiseThreaded(force));
      t.IsBackground = true;
      t.Start();
    }
    public void ReInitialiseThreaded(bool force = false)
    {
      // Log.Debug("HyperionHandler - Reinitialising");
      // Check if IP is hostname and if previously resolved IP is still valid
      HyperionHostnameCheckResolve();

      if (reInitOnError || force)
      {
        Initialise(force);
      }
    }

    public void Dispose()
    {
      //If connected close any remaining sockets and enabled set effect.
      if (_socket.Connected)
      {
          ClearPrioritiesAtmoLight(50);
        Disconnect();
      }

      //Stop live reconnect so it doesn't start new connect threads.
      if (liveReconnectEnabled)
      {
        liveReconnectEnabled = false;
      }
    }
    public bool IsConnected()
    {
      if (initLock)
      {
        return false;
      }

      return _socket.Connected;
    }

    private void Connect()
    {
      try
      {
        Thread t = new Thread(ConnectThread);
        t.IsBackground = true;
        t.Start();
      }
      catch (Exception e)
      {
        // Log.Error("HyperionHandler - Error while starting connect thread");
        // Log.Error("HyperionHandler - Exception: {0}", e.Message);

        //Reset Init lock
        initLock = false;
        isInit = false;
      }
    }
    private void liveReconnect()
    {
      try
      {
        Thread t = new Thread(liveReconnectThread);
        t.IsBackground = true;
        t.Start();
      }
      catch (Exception e)
      {
        // Log.Error("HyperionHandler - Error while starting live reconnect thread");
        // Log.Error("HyperionHandler - Exception: {0}", e.Message);

        //Reset Init lock
        initLock = false;
        isInit = false;
      }
    }

    private void ConnectThread()
    {
      try
      {
        while (hyperionReconnectCounter <= hyperionReconnectAttempts)
        {
          if (!_socket.Connected)
          {
            try
            {
              if (!liveReconnectEnabled)
              {
               // Log.Debug("HyperionHandler - Trying to connect");
              }

              //Close old socket and create new TCP client which allows it to reconnect when calling Connect()
              Disconnect();

              _socket = new TcpClient();
              _socket.SendTimeout = 5000;
              _socket.ReceiveTimeout = 5000;
              _socket.Connect(hyperionIP, hyperionPort);
              _stream = _socket.GetStream();

              if (!liveReconnectEnabled)
              {
               // Log.Debug("HyperionHandler - Connected");
              }
            }
            catch (Exception e)
            {
              if (!liveReconnectEnabled)
              {
                // Log.Error("HyperionHandler - Error while connecting");
                // Log.Error("HyperionHandler - Exception: {0}", e.Message);

                // Check if IP is hostname and if previously resolved IP is still valid
                HyperionHostnameCheckResolve();
              }
            }

            //if live connect enabled don't use this loop and let liveReconnectThread() fire up new connections
            if (liveReconnectEnabled)
            {
              break;
            }
            else
            {
              //Increment times tried
              hyperionReconnectCounter++;

              //Show error if reconnect attempts exhausted
              if (hyperionReconnectCounter > hyperionReconnectAttempts && !_socket.Connected)
              {
                // Log.Error("HyperionHandler - Error while connecting and connection attempts exhausted");
                break;
              }

              //Sleep for specified time
              Thread.Sleep(hyperionReconnectDelay);
            }
            // Log.Error("HyperionHandler - retry attempt {0} of {1}",hyperionReconnectCounter,hyperionReconnectAttempts);
          }
          else
          {
            // Log.Debug("HyperionHandler - Connected after {0} attempts.", hyperionReconnectCounter);
            break;
          }
        }

        //Reset Init lock
        initLock = false;

        //Reset counter when we have finished
        hyperionReconnectCounter = 0;

        if (isInit && _socket.Connected)
        {
          isInit = false;
        }
        else if (isInit)
        {
          isInit = false;
        }
      }
      catch (Exception e)
      {
        // Log.Error("HyperionHandler - Error during connect thread");
        // Log.Error("HyperionHandler - Exception: {0}", e.Message);

        //Reset Init lock
        initLock = false;
        isInit = false;
      }
    }
    private void liveReconnectThread()
    {
      liveReconnectSW.Start();

      //Start live reconnect with set delay in config
      while (liveReconnectEnabled)
      {
        if (liveReconnectSW.ElapsedMilliseconds >= hyperionReconnectDelay && _socket.Connected == false)
        {
          Connect();
          liveReconnectSW.Restart();
        }
      }

      liveReconnectSW.Stop();
    }
    private void Disconnect()
    {
      try
      {
        _socket.Close();
      }
      catch (Exception e)
      {
        // Log.Error(string.Format("HyperionHandler - {0}", "Error during disconnect"));
        // Log.Error(string.Format("HyperionHandler - {0}", e.Message));
      }
    }


    public void ChangeColor(int red, int green, int blue)
    {
      if (!IsConnected())
      {
        return;
      }

      ColorRequest colorRequest = ColorRequest.CreateBuilder()
        .SetRgbColor((red * 256 * 256) + (green * 256) + blue)
        .SetPriority(hyperionPriorityStaticColor)
        .SetDuration(-1)
        .Build();

      HyperionRequest request = HyperionRequest.CreateBuilder()
        .SetCommand(HyperionRequest.Types.Command.COLOR)
        .SetExtension(ColorRequest.ColorRequest_, colorRequest)
        .Build();

      SendRequest(request);
    }
    public void ClearPriority(int priority)
    {
      try
      {
        if (!IsConnected())
        {
          return;
        }

        if (priority == hyperionPriority)
        {
          priorityCleared = true;
        }

        ClearRequest clearRequest = ClearRequest.CreateBuilder()
        .SetPriority(priority)
        .Build();

        HyperionRequest request = HyperionRequest.CreateBuilder()
        .SetCommand(HyperionRequest.Types.Command.CLEAR)
        .SetExtension(ClearRequest.ClearRequest_, clearRequest)
        .Build();

        SendRequest(request);
      }
      catch (Exception e)
      {
        // Log.Error(string.Format("HyperionHandler - {0}", "Error clearing priority"));
        // Log.Error(string.Format("HyperionHandler - {0}", e.Message));
      }
    }
    public void ClearAll()
    {
      if (!IsConnected())
      {
        return;
      }
      HyperionRequest request = HyperionRequest.CreateBuilder()
      .SetCommand(HyperionRequest.Types.Command.CLEARALL)
      .Build();

      SendRequest(request);
    }
    public void ClearPrioritiesAtmoLight(int delay)
    {
      ClearPriority(hyperionPriorityStaticColor);
      Thread.Sleep(delay);
      ClearPriority(hyperionPriority);
    }
    public void ChangeImage(byte[] pixeldata, byte[] bmiInfoHeader)
    {
      if (!IsConnected())
      {
        return;
      }
      // Hyperion expects the bytestring to be the size of 3*width*height.
      // So 3 bytes per pixel, as in RGB.
      // Given pixeldata however is 4 bytes per pixel, as in RGBA.
      // So we need to remove the last byte per pixel.
      byte[] newpixeldata = new byte[captureHeight * captureWidth * 3];
      int x = 0;
      int i = 0;
      while (i <= (newpixeldata.GetLength(0) - 2))
      {
        newpixeldata[i] = pixeldata[i + x + 2];
        newpixeldata[i + 1] = pixeldata[i + x + 1];
        newpixeldata[i + 2] = pixeldata[i + x];
        i += 3;
        x++;
      }

      ImageRequest imageRequest = ImageRequest.CreateBuilder()
        .SetImagedata(Google.ProtocolBuffers.ByteString.CopyFrom(newpixeldata))
        .SetImageheight(captureHeight)
        .SetImagewidth(captureWidth)
        .SetPriority(hyperionPriority)
        .SetDuration(-1)
        .Build();

      HyperionRequest request = HyperionRequest.CreateBuilder()
        .SetCommand(HyperionRequest.Types.Command.IMAGE)
        .SetExtension(ImageRequest.ImageRequest_, imageRequest)
        .Build();

      SendRequest(request);
    }

    private void SendRequest(HyperionRequest request)
    {
      try
      {
        if (_socket.Connected)
        {
          int size = request.SerializedSize;

          Byte[] header = new byte[4];
          header[0] = (byte)((size >> 24) & 0xFF);
          header[1] = (byte)((size >> 16) & 0xFF);
          header[2] = (byte)((size >> 8) & 0xFF);
          header[3] = (byte)((size) & 0xFF);

          int headerSize = header.Count();
          _stream.Write(header, 0, headerSize);
          request.WriteTo(_stream);
          _stream.Flush();

          // Enable reply message if needed (debugging only)
          // HyperionReply reply = receiveReply();
        }
      }
      catch (Exception e)
      {
        // Log.Error("HyperionHandler - Error while sending proto request");
        // Log.Error("HyperionHandler - Exception: {0}", e.Message);

        ReInitialise(false);
      }
    }

    private HyperionReply receiveReply()
    {
      try
      {
        Stream input = _socket.GetStream();
        byte[] header = new byte[4];
        input.Read(header, 0, 4);
        int size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | (header[3]);
        byte[] data = new byte[size];
        input.Read(data, 0, size);
        HyperionReply reply = HyperionReply.ParseFrom(data);
        // Log.Error(reply.ToString());
        return reply;
      }
      catch (Exception e)
      {
        // Log.Error("HyperionHandler - Error while receiving reply from proto request");
        // Log.Error("HyperionHandler - Exception: {0}", e.Message);

        ReInitialise(false);
        return null;
      }
    }
    #endregion


    #region Hyperion IP / hostname check and resolve
    public void HyperionHostnameCheckResolve()
    {
      try
      {
        string ipOrHostname = "";
        // Log.Error("HyperionHandler - checking and resolving hostname to IP");

        if (hyperionpreviousHostname == "")
        {
          ipOrHostname = hyperionIP;
        }
        else
        {
          ipOrHostname = hyperionpreviousHostname;
        }

        IPHostEntry hostEntry = Dns.GetHostEntry(ipOrHostname);

        // IP address
        if (hostEntry.AddressList[0].ToString() == ipOrHostname)
        {
          if (!liveReconnectEnabled)
          {
           // Log.Debug("HyperionHandler - connection method is IP");
          }
        }
        // HOSTNAME 
        else
        {
          string resolvedIP = hostEntry.AddressList[0].ToString();

          if (string.IsNullOrEmpty(resolvedIP) == false)
          {
            if (!liveReconnectEnabled)
            {
             // Log.Debug("HyperionHandler - connection method is HOSTNAME and IP is resolved to: " + resolvedIP);
            }

            // Store current hostname in case we need it later to lookup on reInit (i.e. IP changed)
            hyperionpreviousHostname = hyperionIP;

            // Replace hostname with IP address
            hyperionIP = resolvedIP;
          }
          else
          {
            if (!liveReconnectEnabled)
            {
             // Log.Debug("HyperionHandler - Error while resolving to Hostname to IP addres, returned: " + resolvedIP);
            }
          }
        }
      }
      catch (Exception e)
      {
        if (!liveReconnectEnabled)
        {
          // Log.Error("HyperionHandler - Error while checking IP for hostname string");
          // Log.Error("HyperionHandler - Exception: {0}", e.Message);
        }
      }
    }

    #endregion
  }
}