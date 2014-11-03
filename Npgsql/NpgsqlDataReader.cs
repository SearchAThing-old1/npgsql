using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using Npgsql.Localization;
using Npgsql.Messages;
using NpgsqlTypes;

namespace Npgsql
{
    public class NpgsqlDataReader : DbDataReader
    {
        internal NpgsqlCommand Command { get; private set; }
        NpgsqlConnector _connector;
        NpgsqlConnection _connection;
        CommandBehavior _behavior;

        // TODO: Protect with Interlocked?
        internal ReaderState State { get; private set; }

        RowDescriptionMessage _rowDescription;
        DataRowMessageBase _row;
        int _recordsAffected;
        internal long? LastInsertedOID { get; private set; }

        /// <summary>
        /// Indicates that at least one row has been read for any result set.
        /// </summary>
        bool _readOneRow;

        /// <summary>
        /// Is raised whenever Close() is called.
        /// </summary>
        public event EventHandler ReaderClosed;

        static readonly ILog _log = LogManager.GetCurrentClassLogger();

        internal NpgsqlDataReader(NpgsqlCommand command, CommandBehavior behavior, RowDescriptionMessage rowDescription = null)
        {
            Command = command;
            _connector = command.Connector;
            _connection = command.Connection;
            _behavior = behavior;
            _rowDescription = rowDescription;
            _recordsAffected = -1;
        }

        #region Read

        public override bool Read()
        {
            if (_row != null) {
                _row.Consume();
                _row = null;
            }

            switch (State)
            {
                case ReaderState.InResult:
                    break;
                case ReaderState.BetweenResults:
                case ReaderState.Consumed:
                case ReaderState.Closed:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            if ((_behavior & CommandBehavior.SingleRow) != 0 && _readOneRow)
            {
                // TODO: See optimization proposal in #410
                var completeMsg = _connector.SkipUntil<CommandCompleteMessage>(BackEndMessageCode.CompletedResponse);
                ProcessRead(completeMsg);
                return false;
            }

            while (true)
            {
                var msg = _connector.ReadSingleMessage((_behavior & CommandBehavior.SequentialAccess) != 0);
                switch (ProcessRead(msg))
                {
                    case ReadResult.RowRead:
                        return true;
                    case ReadResult.RowNotRead:
                        return false;
                    case ReadResult.ReadAgain:
                        continue;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        ReadResult ProcessRead(IServerMessage msg)
        {
            switch (msg.Code)
            {
                case BackEndMessageCode.RowDescription:
                    if (_rowDescription != null) {
                        _log.Warn("Received RowDescription but already have one");
                    }
                    _rowDescription = (RowDescriptionMessage)msg;
                    return ReadResult.ReadAgain;

                case BackEndMessageCode.DataRow:
                    if (_rowDescription == null) {
                        throw new Exception("Got DataRow but have no RowDescription");
                    }
                    _row = (DataRowMessageBase)msg;
                    _row.Description = _rowDescription;
                    _readOneRow = true;
                    _connector.State = ConnectorState.Fetching;
                    return ReadResult.RowRead;

                case BackEndMessageCode.CompletedResponse:
                    var completed = (CommandCompleteMessage) msg;
                    if (completed.RowsAffected.HasValue)
                    {
                        _recordsAffected = _recordsAffected == -1
                            ? completed.RowsAffected.Value
                            : _recordsAffected + completed.RowsAffected.Value;
                    }
                    if (completed.LastInsertedOID.HasValue) {
                        LastInsertedOID = completed.LastInsertedOID;
                    }
                    goto case BackEndMessageCode.EmptyQueryResponse;

                case BackEndMessageCode.EmptyQueryResponse:
                    _row = null;
                    _rowDescription = null;
                    State = ReaderState.BetweenResults;
                    return ReadResult.ReadAgain;

                case BackEndMessageCode.ReadyForQuery:
                    State = ReaderState.Consumed;
                    return ReadResult.RowNotRead;

                default:
                    throw new Exception("Received unexpected backend message of type " + msg.Code);
            }
        }

        #endregion

        #region NextResult

        public override bool NextResult()
        {
            switch (State)
            {
                case ReaderState.InResult:
                    if (_row != null)
                    {
                        _row.Consume();
                        _row = null;
                    }
                    // TODO: Duplication with SingleResult handling above
                    var completedMsg = _connector.SkipUntil<CommandCompleteMessage>(BackEndMessageCode.CompletedResponse);
                    ProcessRead(completedMsg);
                    _rowDescription = null;
                    break;

                case ReaderState.BetweenResults:
                    break;

                case ReaderState.Consumed:
                case ReaderState.Closed:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var msg = _connector.ReadSingleMessage();
            if (msg is ReadyForQueryMessage) {
                State = ReaderState.Consumed;
                return false;
            }
            Debug.Assert(msg is RowDescriptionMessage);
            _rowDescription = (RowDescriptionMessage)msg;
            State = ReaderState.InResult;
            return true;
        }

        #endregion

        public override int Depth
        {
            get { throw new NotImplementedException(); }
        }

        /// <summary>
        /// Gets a value indicating whether the data reader is closed.
        /// </summary>
        public override bool IsClosed
        {
            get { return State == ReaderState.Closed; }
        }

        public override int RecordsAffected
        {
            get { return _recordsAffected; }
       }

        public override bool HasRows
        {
            get { throw new NotImplementedException(); }
        }

        public override string GetName(int ordinal)
        {
            throw new NotImplementedException();
        }

        public override int FieldCount
        {
            get { throw new NotImplementedException(); }
        }

        private void CleanUp(bool finishedMessages)
        {
            switch (State)
            {
                case ReaderState.Consumed:
                case ReaderState.Closed:
                    return;
            }

            if ((_behavior & CommandBehavior.SequentialAccess) != 0)
            {
                //throw new NotImplementedException();
            }
            else
            {
                _rowDescription = null;
                if (_row != null)
                {
                    _row.Consume();
                    _row = null;
                }
                _connector.SkipUntil<ReadyForQueryMessage>(BackEndMessageCode.ReadyForQuery);
            }
            State = ReaderState.Consumed;
        }

        public override void Close()
        {
            CleanUp(false);
            if ((_behavior & CommandBehavior.CloseConnection) != 0) {
                _connection.Close();
            }
            State = ReaderState.Closed;
            _connector.State = ConnectorState.Ready;
            if (ReaderClosed != null) {
                ReaderClosed(this, EventArgs.Empty);
            }
        }

        public override DataTable GetSchemaTable()
        {
            throw new NotImplementedException();
        }

        void CheckHasRow()
        {
            if (_row == null) {
                throw new InvalidOperationException("Invalid attempt to read when no data is present.");
            }            
        }

        #region Value getters

        public override bool GetBoolean(int ordinal)
        {
            CheckHasRow();
            return _row.Get(ordinal).Boolean;
        }

        public override byte GetByte(int ordinal)
        {
            CheckHasRow();
            return _row.Get(ordinal).Byte;
        }

        public override char GetChar(int ordinal)
        {
            throw new NotImplementedException();
        }

        public override Guid GetGuid(int ordinal)
        {
            throw new NotImplementedException();
        }

        public override short GetInt16(int ordinal)
        {
            CheckHasRow();
            return _row.Get(ordinal).Int16;
        }

        public override int GetInt32(int ordinal)
        {
            CheckHasRow();
            return _row.Get(ordinal).Int32;
        }

        public override long GetInt64(int ordinal)
        {
            CheckHasRow();
            return _row.Get(ordinal).Int64;
        }

        public override DateTime GetDateTime(int ordinal)
        {
            throw new NotImplementedException();
        }

        public override string GetString(int ordinal)
        {
            CheckHasRow();
            return _row.Get(ordinal).String;
        }

        public override decimal GetDecimal(int ordinal)
        {
            throw new NotImplementedException();
        }

        public override double GetDouble(int ordinal)
        {
            throw new NotImplementedException();
        }

        public override float GetFloat(int ordinal)
        {
            throw new NotImplementedException();
        }

        public override long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length)
        {
            throw new NotImplementedException();
        }

        public override long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
        {
            throw new NotImplementedException();
        }

        public override int GetValues(object[] values)
        {
            throw new NotImplementedException();
        }

        public override object this[int ordinal]
        {
            get
            {
                CheckHasRow();
                return _row.Get(ordinal).Object;
            }
        }

#if OLD
        public override object this[int ordinal]
        {
            get { return GetValueInternal(ordinal); }
        }

        object GetValueInternal(int ordinal)
        {
            object providerValue = GetProviderSpecificValue(ordinal);
            NpgsqlBackendTypeInfo backendTypeInfo;
            if (Command.ExpectedTypes != null && Command.ExpectedTypes.Length > ordinal && Command.ExpectedTypes[ordinal] != null)
            {
                return ExpectedTypeConverter.ChangeType(providerValue, Command.ExpectedTypes[ordinal]);
            }
            else if ((_connection == null || !_connection.UseExtendedTypes) && TryGetTypeInfo(ordinal, out backendTypeInfo))
                return backendTypeInfo.ConvertToFrameworkType(providerValue);
            return providerValue;
        }

        public override object GetProviderSpecificValue(int ordinal)
        {
            throw new NotImplementedException();
            var field = SeekToColumn(ordinal);
            var len = _row.Buffer.ReadInt32();

            if (field.FormatCode == FormatCode.Text)
            {
                //return
                    //NpgsqlTypesHelper.ConvertBackendStringToSystemType(field.TypeInfo, _row.Buffer, len,
                    //                                                   field.TypeModifier);
            }
            else
            {
                //return
                    //NpgsqlTypesHelper.ConvertBackendBytesToSystemType(field.TypeInfo, _row.Buffer, len,
                    //                                                  field.TypeModifier);
            }
        }

        internal bool TryGetTypeInfo(int fieldIndex, out NpgsqlBackendTypeInfo backendTypeInfo)
        {
            if (_rowDescription == null)
            {
                throw new IndexOutOfRangeException(); //Essentially, all indices are out of range.
            }
            return (backendTypeInfo = _rowDescription[fieldIndex].TypeInfo) != null;
        }
#endif

        #endregion

        #region Non-standard value getters

        public NpgsqlDate GetDate(int ordinal)
        {
            throw new NotImplementedException();
        }

        public NpgsqlTime GetTime(int ordinal)
        {
            throw new NotImplementedException();
        }

        public NpgsqlTimeTZ GetTimeTZ(int ordinal)
        {
            throw new NotImplementedException();
        }

        public TimeSpan GetTimeSpan(int ordinal)
        {
            throw new NotImplementedException();
        }

        public NpgsqlTimeStamp GetTimeStamp(int ordinal)
        {
            throw new NotImplementedException();
        }

        public NpgsqlTimeStampTZ GetTimeStampTZ(int ordinal)
        {
            throw new NotImplementedException();
        }

        #endregion

        public override bool IsDBNull(int ordinal)
        {
            CheckHasRow();
            return _row.Get(ordinal).IsNull;
        }

        public override object this[string name]
        {
            get { throw new NotImplementedException(); }
        }

        public override int GetOrdinal(string name)
        {
            throw new NotImplementedException();
        }

        public override string GetDataTypeName(int ordinal)
        {
            throw new NotImplementedException();
        }

        public override Type GetFieldType(int ordinal)
        {
            throw new NotImplementedException();
        }

        public override object GetValue(int ordinal)
        {
            return this[ordinal];
        }

        public override IEnumerator GetEnumerator()
        {
            throw new NotImplementedException();
        }
    }

    enum ReaderState
    {
        InResult,
        BetweenResults,
        Consumed,
        Closed
    }

    enum ReadResult
    {
        RowRead,
        RowNotRead,
        ReadAgain,
    }
}
