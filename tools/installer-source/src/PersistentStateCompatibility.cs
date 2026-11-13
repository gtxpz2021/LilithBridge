using System;
using System.IO;
using System.Text;
using System.Linq;

namespace LilithSetup
{
    // Header/reader preflight only. This does not migrate data, load product code,
    // validate all row contents, or claim that a DLL backup is a data snapshot.
    internal static class PersistentStateCompatibility
    {
        const string Candidate = "caf0be7e84531a1922cf5eb5c300f57434fcbd37286cb1540c4001aab2c80325";
        const string BaseUnified = "6148f5f9139d998cd26ddb877ff12c1b760d3395dbe21d04bb150cfaa4855a3a";
        const string IdleCompat = "1e7011343ce0e601224ce3bd794b61197fce03aa5576d20ff88b331cfcd0a249";
        static bool KnownCurrentReader(string hash) { return hash==Candidate || hash==BaseUnified || hash==IdleCompat || hash=="f7c67ee6f305eda6758f546b6eac34d7a4a211095ead0d481fbd2342e32d4a30" || hash=="4b53c5366d77ed61b827d69380d04cdb729a1d93cdf98848d07e2cf61ca60e80" || hash=="89c86d1fb43132d0ed46037ae6d15b0906979d7b6a30353c3d1257203e997c9e" || hash=="9ce6835396b4fd97925cdbd0685399f4bcb6336977ca762137c2c4c80cbd697e" || hash=="83168a8fd166b87c56d0ebc3fa9a5cae0f045b2f07b5622e38cb180a3bc54dda" || hash=="afd03ea7b66f8ad3b5d2513467803e326c40253f3d742d7f681b2a6e74fd4ca7" || hash=="e4c865ca691559e8b9fe784c88cc7c491f825e32f8cf051b0496f027319152ff" || hash == "bfb40b65f2bfeb2963cb753967e6d6d8cb5fac864896103927c1994c321858e6"; }
        const string PublicFix2 = "08cef63c6315d9564f59f8d97b510c4671e4eb5ed7e71ca3e21fde76f8755bd7";
        static FileAttributes? Attributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        static FileAttributes? SafeAttributes(string path)
        {
            for (string p=path;!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
            {
                var a=Attributes(p);
                SetupCore.Require(!a.HasValue || (a.Value&FileAttributes.ReparsePoint)==0,"LBINSTALL_DATA_STATE_UNKNOWN");
            }
            return Attributes(path);
        }
        static bool Exists(string path) { return SafeAttributes(path).HasValue; }
        static bool HasNewFiles(string path)
        {
            var a=SafeAttributes(path);if(!a.HasValue)return false;
            SetupCore.Require((a.Value&FileAttributes.Directory)!=0,"LBINSTALL_DATA_STATE_UNKNOWN");
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        static int BigEndian(byte[] b,int at) { return (b[at]<<24)|(b[at+1]<<16)|(b[at+2]<<8)|b[at+3]; }
        internal static string Check(string root,string imageHash)
        {
            string work=ReflectionWorkCompatibility.Check(root,imageHash);
            try
            {
                root=SetupCore.SafePath(root,null);
                SetupCore.Require(!Exists(Path.Combine(root,".reflection-purpose-transfer.lock")),"LBINSTALL_DATA_TRANSFER_PENDING");
                bool generated=HasNewFiles(Path.Combine(root,@"sessions\generated-public-v1"));
                bool transfer=HasNewFiles(Path.Combine(root,"reflection-purpose-transfers-v1"));
                SetupCore.Require(KnownCurrentReader(imageHash) || !generated&&!transfer,"LBINSTALL_NEW_DATA_TARGET_UNVERIFIED");
                string database=Path.Combine(root,"v4-b1-continuity-v2.sqlite3");int version=0;
                var a=SafeAttributes(database);
                if(a.HasValue)
                {
                    SetupCore.Require((a.Value&FileAttributes.Directory)==0,"LBINSTALL_DATA_STATE_UNKNOWN");
                    // A journal needs the runtime's recovery policy, never a header guess.
                    foreach(string suffix in new[]{"-journal","-wal","-shm"})
                        SetupCore.Require(!Exists(database+suffix),"LBINSTALL_DATA_STATE_UNKNOWN");
                    byte[] header=new byte[100];
                    using(var stream=new FileStream(database,FileMode.Open,FileAccess.Read,FileShare.Read))
                        SetupCore.Require(stream.Read(header,0,100)==100,"LBINSTALL_DATA_STATE_UNKNOWN");
                    SetupCore.Require(header.Take(16).SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0"))
                        &&BigEndian(header,68)==1279406386&&header[18]==1&&header[19]==1,"LBINSTALL_DATA_STATE_UNKNOWN");
                    version=BigEndian(header,60);
                    SetupCore.Require(version==2||version==3||version==4,"LBINSTALL_DATA_STATE_UNKNOWN");
                    // Bound to reviewed readers, not version strings or previous hashes.
                    SetupCore.Require(KnownCurrentReader(imageHash) || imageHash==PublicFix2&&version==2,"LBINSTALL_B1_TARGET_UNVERIFIED");
                }
                return work+"|b1="+version+"|generated="+generated+"|transfer="+transfer;
            }
            catch(SetupException){throw;}
            catch {throw new SetupException("LBINSTALL_DATA_STATE_UNKNOWN");}
        }
    }
}
