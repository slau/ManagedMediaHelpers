﻿//-----------------------------------------------------------------------
// <copyright file="Mp3MediaStreamSource.cs" company="Larry Olson">
// (c) Copyright Larry Olson.
//
// Changes to support duration and streaming (i.e. non-seekable) content
// (c) Copyright 2010 Rdio.
//
// This source is subject to the Microsoft Public License (Ms-PL)
// See http://code.msdn.microsoft.com/ManagedMediaHelpers/Project/License.aspx
// All other rights reserved.
// </copyright>
//-----------------------------------------------------------------------

[module: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming",
    "CA1709:IdentifiersShouldBeCasedCorrectly",
    Scope = "type",
    Target = "Media.Mp3MediaStreamSource",
    MessageId = "Mp",
    Justification = "Mp is not a two letter acyonym but is instead part of Mp3")]

/**
 * Presents a byte stream as mp3 frames for the Media Element.  
 * Based on ManagedMediaHelpers code sample from http://code.msdn.microsoft.com/ManagedMediaHelpers/
 * 
 * Tweaked to work on WinPho. Changes include BitTools.ToLittleEndianString
 * and setting the duration properly based on bit rate & content length.
 * Also added support for non-seekable content and to be more memory-efficient
 * to avoid copying the entire audio stream's data into memory.
 */

namespace Media
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Windows.Media;
    using System.Windows.Threading;
    using System.Diagnostics;
    using System.Security.Cryptography;
    using MediaParsers;

    /// <summary>
    /// A Simple MediaStreamSource which can play back MP3 streams from
    /// beginning to end.
    /// </summary>
    public class Mp3MediaStreamSource : MediaStreamSource
    {
        /// <summary>
        ///  ID3 version 1 tags are 128 bytes at the end of the file.
        ///  http://www.id3.org/ID3v1
        /// </summary>
        private const int Id3Version1TagSize = 128;

        /// <summary>
        /// The Mp3 stream being played back.
        /// </summary>
        private Stream audioStream;

        /// <summary>
        /// Description of the Mp3 Stream being played back which includes the
        /// MpegLayer3WaveFormat structure serialized out as a string of hex 
        /// characters.
        /// </summary>
        private MediaStreamDescription audioStreamDescription;

        /// <summary>
        /// The position in the stream where the current MpegFrame starts.
        /// For purposes of this code, the frame starts with the header and
        /// not after the header.
        /// </summary>
        private long currentFrameStartPosition;

        /// <summary>
        /// The length of the audiostream as determined via the constructors.
        /// </summary>
        private long audioStreamLength;
        
        /// <summary>
        /// Initializes a new instance of the Mp3MediaStreamSource class.
        /// </summary>
        /// <param name="audioStream">
        /// Seekable stream containing Mp3 data
        /// </param>
        public Mp3MediaStreamSource(Stream audioStream)
        {
            this.audioStream = audioStream;
            this.audioStreamLength = audioStream.Length;
        }

        /// <summary>
        /// Initialises a new instance of the Mp3MediaStreamSource with a pre-determined length.
        /// This is useful for wrapping an IO stream that may not be seekable (and thus won't have a .Length)
        /// but for which you already know the length (e.g. a CryptoStream from an IsolatedStorageFileStream with a byte count,
        /// or an HTTP stream which has a specified content-length)
        /// </summary>
        public Mp3MediaStreamSource(Stream audioStream, long length)
        {
            this.audioStream = audioStream;
            this.audioStreamLength = length;
        }

        /// <summary>
        /// Gets the MpegLayer3WaveFormat structure which represents this Mp3 file.
        /// </summary>
        public MpegLayer3WaveFormat MpegLayer3WaveFormat { get; private set; }

        /// <summary>
        /// Holds the duration of the track
        /// </summary>
        private TimeSpan trackDuration;

        /// <summary>
        /// The current frame to parse
        /// </summary>
        private MpegFrame currentFrame;

        /// <summary>
        /// Buffer for decoding audio frames into.  4096 should be larger than we'll ever need, right? (144*448*1000/44100)
        /// </summary>
        private static byte[] buffer = new byte[4096];

        /// <summary>
        /// Parses the passed in MediaStream to find the first frame and signals
        /// to its parent MediaElement that it is ready to begin playback by calling
        /// ReportOpenMediaCompleted.
        /// </summary>
        protected override void OpenMediaAsync()
        {
            // Initialize data structures to pass to the Media pipeline via the MediaStreamSource
            Dictionary<MediaSourceAttributesKeys, string> mediaSourceAttributes = new Dictionary<MediaSourceAttributesKeys, string>();
            Dictionary<MediaStreamAttributeKeys, string> mediaStreamAttributes = new Dictionary<MediaStreamAttributeKeys, string>();
            List<MediaStreamDescription> mediaStreamDescriptions = new List<MediaStreamDescription>();

            // Mp3 frame validity check.
            MpegFrame mpegLayer3Frame = new MpegFrame(this.audioStream);
            if (mpegLayer3Frame.FrameSize <= 0)
            {
                throw new InvalidOperationException("MpegFrame's FrameSize cannot be negative");
            }

            // Initialize the Mp3 data structures used by the Media pipeline with state from the first frame.
            WaveFormatExtensible wfx = new WaveFormatExtensible();
            this.MpegLayer3WaveFormat = new MpegLayer3WaveFormat();
            this.MpegLayer3WaveFormat.WaveFormatExtensible = wfx;

            this.MpegLayer3WaveFormat.WaveFormatExtensible.FormatTag = 85;
            this.MpegLayer3WaveFormat.WaveFormatExtensible.Channels = (short)((mpegLayer3Frame.Channels == Channel.SingleChannel) ? 1 : 2);
            this.MpegLayer3WaveFormat.WaveFormatExtensible.SamplesPerSec = mpegLayer3Frame.SamplingRate;
            this.MpegLayer3WaveFormat.WaveFormatExtensible.AverageBytesPerSecond = mpegLayer3Frame.Bitrate / 8;
            this.MpegLayer3WaveFormat.WaveFormatExtensible.BlockAlign = 1;
            this.MpegLayer3WaveFormat.WaveFormatExtensible.BitsPerSample = 0;
            this.MpegLayer3WaveFormat.WaveFormatExtensible.Size = 12;

            this.MpegLayer3WaveFormat.Id = 1;
            this.MpegLayer3WaveFormat.BitratePaddingMode = 0;
            this.MpegLayer3WaveFormat.FramesPerBlock = 1;
            this.MpegLayer3WaveFormat.BlockSize = (short)mpegLayer3Frame.FrameSize;
            this.MpegLayer3WaveFormat.CodecDelay = 0;

            mediaStreamAttributes[MediaStreamAttributeKeys.CodecPrivateData] = this.MpegLayer3WaveFormat.ToHexString();
            this.audioStreamDescription = new MediaStreamDescription(MediaStreamType.Audio, mediaStreamAttributes);

            mediaStreamDescriptions.Add(this.audioStreamDescription);

            trackDuration = new TimeSpan(0, 0, (int)(this.audioStreamLength / MpegLayer3WaveFormat.WaveFormatExtensible.AverageBytesPerSecond));
            mediaSourceAttributes[MediaSourceAttributesKeys.Duration] = trackDuration.Ticks.ToString(CultureInfo.InvariantCulture);
            if (audioStream.CanSeek)
                mediaSourceAttributes[MediaSourceAttributesKeys.CanSeek] = "1";
            else
                mediaSourceAttributes[MediaSourceAttributesKeys.CanSeek] = "0";

            // Report that the Mp3MediaStreamSource has finished initializing its internal state and can now
            // pass in Mp3 Samples.
            this.ReportOpenMediaCompleted(mediaSourceAttributes, mediaStreamDescriptions);

            this.currentFrame = mpegLayer3Frame;
            this.currentFrameStartPosition = MpegFrame.FrameHeaderSize;
        }

        /// <summary>
        /// Parses the next sample from the requested stream and then calls ReportGetSampleCompleted
        /// to inform its parent MediaElement of the next sample.
        /// </summary>
        /// <param name="mediaStreamType">
        /// Should always be Audio for this MediaStreamSource.
        /// </param>
        protected override void GetSampleAsync(MediaStreamType mediaStreamType)
        {
            Dictionary<MediaSampleAttributeKeys, string> emptyDict = new Dictionary<MediaSampleAttributeKeys, string>();
            MediaStreamSample audioSample = null;

            if (this.currentFrame != null)
            {
                // Calculate our current position
                double ratio = (double)this.currentFrameStartPosition / (double)this.audioStreamLength;
                TimeSpan currentPosition = new TimeSpan((long)(trackDuration.Ticks * ratio));

                // Create a MemoryStream to hold the bytes
                // FrameSize includes the frame header which we've already read from the previous iteration, so just copy the
                // header, and then read the remaining bytes
                this.currentFrame.CopyHeader(buffer, 0);
                int audioSampleSize = this.currentFrame.FrameSize - MpegFrame.FrameHeaderSize;
                int c = this.audioStream.Read(buffer, MpegFrame.FrameHeaderSize, audioSampleSize);
                if (c != audioSampleSize)
                {
                    // Ran out of bytes trying to read MP3 frame.
                    this.currentFrame = null;
                    audioSample = new MediaStreamSample(this.audioStreamDescription, null, 0, 0, 0, emptyDict);
                    this.ReportGetSampleCompleted(audioSample);
                    return;
                }
                this.currentFrameStartPosition += c;
                MemoryStream audioFrameStream = new MemoryStream(buffer);

                // Return the next sample in the stream
                audioSample = new MediaStreamSample(this.audioStreamDescription, audioFrameStream, 0, this.currentFrame.FrameSize, currentPosition.Ticks, emptyDict);
                this.ReportGetSampleCompleted(audioSample);

                // Grab the next frame
                MpegFrame nextFrame = new MpegFrame(this.audioStream);
                if (nextFrame.Version == 1 && nextFrame.Layer == 3)
                {
                    this.currentFrameStartPosition += MpegFrame.FrameHeaderSize;
                    this.currentFrame = nextFrame;
                }
                else
                {
                    this.currentFrame = null;
                }
            }
            else
            {
                // We're near the end of the file, or we got an irrecoverable error.
                // Return a null stream which tells the MediaStreamSource & MediaElement to shut down
                audioSample = new MediaStreamSample(this.audioStreamDescription, null, 0, 0, 0, emptyDict);
                this.ReportGetSampleCompleted(audioSample);
            }
        }

        /// <summary>
        ///  TODO FILL ME IN LATER
        /// </summary>
        protected override void CloseMedia()
        {
            try
            {
                audioStream.Close();
            }
            catch (CryptographicException)
            {
                // Ignore these, they are thrown when abruptly closing a
                // stream (i.e. skipping tracks) where the source is a
                // CryptoStream
            }
            catch (Exception e)
            {
                // Should probably log these somewhere
            }
        }

        /// <summary>
        ///  TODO FILL ME IN LATER
        /// </summary>
        /// <param name="diagnosticKind">
        ///  TODO FILL ME IN LATER . . .
        /// </param>
        protected override void GetDiagnosticAsync(MediaStreamSourceDiagnosticKind diagnosticKind)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// <para>
        /// Effectively a Null-Op for when a MediaElement requests a seek at the beginning
        /// of the stream. This makes the stream semi-unseekable.
        /// </para>
        /// <para>
        /// In a fuller MediaStreamSource, the logic here would be to actually seek to
        /// the correct mpeg frame matching the seekToTime passed in.
        /// </para>
        /// </summary>
        /// <param name="seekToTime">
        ///  The time to seek to in nanosecond ticks.
        /// </param>
        protected override void SeekAsync(long seekToTime)
        {
            this.ReportSeekCompleted(seekToTime);
        }

        /// <summary>
        ///  TODO FILL ME IN LATER
        /// </summary>
        /// <param name="mediaStreamDescription">
        ///  TODO FILL ME IN LATER . . .
        /// </param>
        protected override void SwitchMediaStreamAsync(MediaStreamDescription mediaStreamDescription)
        {
            throw new NotImplementedException();
        }
    }
}
