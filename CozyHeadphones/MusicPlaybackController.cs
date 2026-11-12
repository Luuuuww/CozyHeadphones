
using System;
using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;

namespace CozyHeadphones
{
    internal sealed class MusicPlaybackController : IDisposable
    {
        private WaveOutEvent? output;
        private WaveStream? reader;
        private bool advanceWhenStopped;

        public bool IsActive => output != null;

        public bool IsPlaying =>
            output?.PlaybackState == PlaybackState.Playing;

        public bool IsPaused =>
            output?.PlaybackState == PlaybackState.Paused;

        public bool HasEnded =>
            advanceWhenStopped &&
            output?.PlaybackState == PlaybackState.Stopped;

        public void Play(string filePath)
        {
            Stop();

            string extension = Path.GetExtension(filePath);

            WaveStream newReader;

            if (extension.Equals(
                ".mp3",
                StringComparison.OrdinalIgnoreCase))
            {
                newReader = new Mp3FileReader(filePath);
            }
            else if (extension.Equals(
                ".ogg",
                StringComparison.OrdinalIgnoreCase))
            {
                newReader = new VorbisWaveReader(filePath);
            }
            else if (extension.Equals(
                ".wav",
                StringComparison.OrdinalIgnoreCase))
            {
                newReader = new WaveFileReader(filePath);
            }
            else
            {
                throw new NotSupportedException(
                    "El formato de audio no es compatible."
                );
            }

            WaveOutEvent newOutput = new()
            {
                DesiredLatency = 150,
                Volume = 0.8f
            };

            try
            {
                newOutput.Init(newReader);

                reader = newReader;
                output = newOutput;
                advanceWhenStopped = true;

                newOutput.Play();
            }
            catch
            {
                newOutput.Dispose();
                newReader.Dispose();
                throw;
            }
        }

        public void Pause()
        {
            output?.Pause();
        }

        public void Resume()
        {
            output?.Play();
        }

        public void Stop()
        {
            advanceWhenStopped = false;

            WaveOutEvent? oldOutput = output;
            WaveStream? oldReader = reader;

            output = null;
            reader = null;

            if (oldOutput != null)
            {
                oldOutput.Stop();
                oldOutput.Dispose();
            }

            oldReader?.Dispose();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
