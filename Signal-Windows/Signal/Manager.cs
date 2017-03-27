﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using libsignalservice.util;
using libsignalservice;
using libsignalservice.push;
using libsignal.util;
using libsignal;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using libsignal.state;
using libsignal.ecc;
using libsignalservice.messages;
using static libsignalservice.SignalServiceMessagePipe;
using libsignalservice.crypto;
using Strilanc.Value;
using Signal_Windows.Storage;
using Windows.Storage;
using System.Threading;
using static libsignalservice.SignalServiceMessageSender;

namespace Signal_Windows.Signal
{
    public class Manager
    {
        ApplicationDataContainer            LocalSettings = ApplicationData.Current.LocalSettings;
        public static string                       localFolder = ApplicationData.Current.LocalFolder.Path;
        static string                       URL         = "https://textsecure-service.whispersystems.org";
        static TrustStore                   TRUST_STORE = new WhisperTrustStore();
        SignalServiceUrl[]                  serviceUrls = new SignalServiceUrl[] { new SignalServiceUrl(URL, TRUST_STORE) };
        public const String                 USER_AGENT  = "Signal-Windows";
        public SignalServiceAccountManager  accountManager;
        public Store                        SignalStore;
        private static uint                 PREKEY_MINIMUM_COUNT = 20;
        private static uint                 PREKEY_BATCH_SIZE = 100;
        private static JsonConverter[]      converters = new JsonConverter[] {
                                                new IdentityKeyPairConverter(),
                                                new IdentityKeyConverter(),
                                                new ByteArrayConverter()};

        CancellationToken Token;
        SignalServiceMessagePipe Pipe = null;
        SignalServiceMessageSender MessageSender;
        public SignalServiceMessageReceiver MessageReceiver;

        public Manager(CancellationToken token, String username, bool active)
        {
            Debug.WriteLine(localFolder);
            this.Token = token;
            try
            {
                Load(username);
                MessageReceiver = new SignalServiceMessageReceiver(Token, serviceUrls, new StaticCredentialsProvider(SignalStore.username, SignalStore.password, SignalStore.signalingKey), USER_AGENT);
                if(active)
                {
                    Pipe = MessageReceiver.createMessagePipe();
                }
                MessageSender = new SignalServiceMessageSender(Token, serviceUrls, SignalStore.username, SignalStore.password, SignalStore, Pipe, null, USER_AGENT);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        }

        public void Shutdown()
        {
            Pipe.Shutdown();
        }

        public void ReceiveBatch(MessagePipeCallback callback)
        {
            try
            {
                Pipe.ReadBlocking(callback);
            } catch(Exception e)
            {
                Debug.WriteLine(e.Message);
                Debug.WriteLine(e.StackTrace);
            }
        }

        public void Save()
        {
            using (FileStream fs = File.Open(localFolder + @"\" + LocalSettings.Values["Username"] + "Store.json", FileMode.OpenOrCreate))
            using (StreamWriter sw = new StreamWriter(fs))
            {
                string s = JsonConvert.SerializeObject(SignalStore, Formatting.Indented, converters);
                sw.Write(s);
            }
        }

        public void Load(string username)
        {
            try
            {
                using (FileStream fs = File.Open(localFolder + @"\" + LocalSettings.Values["Username"] + "Store.json", FileMode.Open))
                using (StreamReader sr = new StreamReader(fs))
                {
                    SignalStore = JsonConvert.DeserializeObject<Store>(sr.ReadToEnd(), converters);
                    string s = JsonConvert.SerializeObject(SignalStore, Formatting.Indented, converters);
                    Debug.WriteLine(s);
                }
            }
            catch(Exception e)
            {
                IdentityKeyPair identityKey = KeyHelper.generateIdentityKeyPair();
                uint registrationId = KeyHelper.generateRegistrationId(false); //TODO why uint
                SignalStore = new Store(identityKey, registrationId);
                SignalStore.registered = false;
                SignalStore.username = (string) LocalSettings.Values["Username"];
                Save();
            }
            accountManager = new SignalServiceAccountManager(serviceUrls, SignalStore.username, SignalStore.password, USER_AGENT);
        }

        public void Register(bool voiceVerification)
        {
            SignalStore.password = Base64.encodeBytes(Util.getSecretBytes(18));
            accountManager = new SignalServiceAccountManager(serviceUrls, SignalStore.username, SignalStore.password, USER_AGENT);
            if (voiceVerification)
                accountManager.requestVoiceVerificationCode();
            else
                accountManager.requestSmsVerificationCode();

            SignalStore.registered = false;
            Save();
        }

        public void VerifyAccount(String verificationCode)
        {
            Debug.WriteLine("VERIFYING " + verificationCode);
            SignalStore.signalingKey = Base64.encodeBytes(Util.getSecretBytes(52));
            accountManager.verifyAccountWithCode(verificationCode, SignalStore.signalingKey, SignalStore.jsonIdentityKeyStore.GetLocalRegistrationId(), true);

            SignalStore.registered = true;
            refreshPreKeys();
            Save();
        }

        public void sendMessage(List<SignalServiceAddress> recipients, SignalServiceDataMessage message)
        {
            MessageSender.sendMessage(recipients, message);
            Save();
        }

        private void refreshPreKeys()
        {
            List<PreKeyRecord> oneTimePreKeys = generatePreKeys();
            PreKeyRecord lastResortKey = getOrGenerateLastResortPreKey();
            SignedPreKeyRecord signedPreKeyRecord = generateSignedPreKey(SignalStore.GetIdentityKeyPair());

            accountManager.setPreKeys(SignalStore.GetIdentityKeyPair().getPublicKey(), lastResortKey, signedPreKeyRecord, oneTimePreKeys);
        }

        private List<PreKeyRecord> generatePreKeys()
        {
            List<PreKeyRecord> records = new List<PreKeyRecord>();
            for (uint i = 0; i < PREKEY_BATCH_SIZE; i++)
            {
                uint preKeyId = (SignalStore.preKeyIdOffset + i) % Medium.MAX_VALUE;
                ECKeyPair keyPair = Curve.generateKeyPair();
                PreKeyRecord record = new PreKeyRecord(preKeyId, keyPair);

                SignalStore.StorePreKey(preKeyId, record);
                records.Add(record);
            }
            SignalStore.preKeyIdOffset = (SignalStore.preKeyIdOffset + PREKEY_BATCH_SIZE + 1) % Medium.MAX_VALUE;
            Save();
            return records;
        }

        private PreKeyRecord getOrGenerateLastResortPreKey()
        {
            if (SignalStore.ContainsPreKey(Medium.MAX_VALUE))
            {
                try
                {
                    return SignalStore.LoadPreKey(Medium.MAX_VALUE);
                }
                catch (InvalidKeyIdException e)
                {
                    SignalStore.RemovePreKey(Medium.MAX_VALUE);
                }
            }
            ECKeyPair keyPair = Curve.generateKeyPair();
            PreKeyRecord record = new PreKeyRecord(Medium.MAX_VALUE, keyPair);
            SignalStore.StorePreKey(Medium.MAX_VALUE, record);
            Save();
            return record;
        }

        private SignedPreKeyRecord generateSignedPreKey(IdentityKeyPair identityKeyPair)
        {
            try
            {
                ECKeyPair keyPair = Curve.generateKeyPair();
                byte[] signature = Curve.calculateSignature(identityKeyPair.getPrivateKey(), keyPair.getPublicKey().serialize());
                SignedPreKeyRecord record = new SignedPreKeyRecord(SignalStore.nextSignedPreKeyId, (ulong) DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond, keyPair, signature);

                SignalStore.StoreSignedPreKey(SignalStore.nextSignedPreKeyId, record);
                SignalStore.nextSignedPreKeyId = (SignalStore.nextSignedPreKeyId + 1) % Medium.MAX_VALUE;
                Save();

                return record;
            }
            catch (InvalidKeyException e)
            {
                throw e;
            }
        }
    }
}