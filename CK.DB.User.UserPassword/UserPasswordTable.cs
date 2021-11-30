using System;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using CK.SqlServer;
using CK.Core;
using CK.DB.Auth;
using System.Threading;

namespace CK.DB.User.UserPassword
{

    /// <summary>
    /// Holds password hashes for users and offer standard strong hash implementation:
    /// PBKDF2 with HMAC-SHA256, 128-bit salt, 256-bit subkey, with a default to 10000 iterations.
    /// Static <see cref="HashIterationCount"/> may be changed (typically at starting time).
    /// </summary>
    [SqlTable("tUserPassword", Package = typeof(Package))]
    [Versions( "1.0.0,1.0.1,1.0.2,1.0.3" )]
    [SqlObjectItem( "transform:sUserDestroy, transform:sAuthUserOnLogin" )]
    public abstract partial class UserPasswordTable : SqlTable, IBasicAuthenticationProvider
    {
        const string _commandReadByUserId = "select p.PwdHash, u.UserId, p.FailedAttemptCount from CK.tUser u left outer join CK.tUserPassword p on p.UserId = u.UserId where u.UserId=@UserId";
        Actor.UserTable _userTable;

        static int _iterationCount;

        /// <summary>
        /// Current iteration count.
        /// Should be changed only at start and only if you know what you are doing.
        /// It can not be less than 5000 and defaults to <see cref="DefaultHashIterationCount"/>.
        /// </summary>
        static public int HashIterationCount
        {
            get { return _iterationCount; }
            set
            {
                if( value < 5000 ) throw new ArgumentException( "HashIterationCount must be at the very least 5000." );
                _iterationCount = value;
            }
        }

        /// <summary>
        /// The default <see cref="HashIterationCount"/>.
        /// </summary>
        public const int DefaultHashIterationCount = 200_000;

        static UserPasswordTable()
        {
            _iterationCount = DefaultHashIterationCount;
        }

        void StObjConstruct( Actor.UserTable userTable )
        {
            _userTable = userTable;
        }

        /// <summary>
        /// Gets the User password package.
        /// </summary>
        [InjectObject]
        public Package UserPasswordPackage { get; protected set; }

        /// <summary>
        /// Associates a PasswordUser to an existing user.
        /// </summary>
        /// <param name="ctx">The call context to use.</param>
        /// <param name="actorId">The acting actor identifier.</param>
        /// <param name="userId">The user identifier that must have a password.</param>
        /// <param name="password">The initial password. Can not be null nor empty.</param>
        /// <param name="mode">Optionnaly configures Create, Update only or WithLogin behavior.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The result.</returns>
        public Task<UCLResult> CreateOrUpdatePasswordUserAsync( ISqlCallContext ctx, int actorId, int userId, string password, UCLMode mode = UCLMode.CreateOrUpdate, CancellationToken cancellationToken = default( CancellationToken ) )
        {
            if( string.IsNullOrEmpty( password ) ) throw new ArgumentNullException( nameof( password ) );
            PasswordHasher p = new PasswordHasher( HashIterationCount );
            return PasswordUserUCLAsync( ctx, actorId, userId, p.HashPassword( password ), mode, null, cancellationToken );
        }

        /// <summary>
        /// Changes the password of a PasswordUser.
        /// </summary>
        /// <param name="ctx">The call context to use.</param>
        /// <param name="actorId">The acting actor identifier.</param>
        /// <param name="userId">The user identifier that must have a new password.</param>
        /// <param name="password">The new password to set. Can not be null nor empty.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The awaitable.</returns>
        public Task SetPasswordAsync( ISqlCallContext ctx, int actorId, int userId, string password, CancellationToken cancellationToken = default( CancellationToken ) )
        {
            if( string.IsNullOrEmpty( password ) ) throw new ArgumentNullException( nameof( password ) );
            PasswordHasher p = new PasswordHasher( HashIterationCount );
            return PasswordUserUCLAsync( ctx, actorId, userId, p.HashPassword( password ), UCLMode.UpdateOnly, null, cancellationToken );
        }

        /// <summary>
        /// Verifies a password for a user identifier.
        /// This automatically updates the hash if the <see cref="HashIterationCount"/> changed
        /// or if the internal algorithm is upgraded.
        /// </summary>
        /// <param name="ctx">The call context to use.</param>
        /// <param name="userId">The user identifier.</param>
        /// <param name="password">The password to challenge.</param>
        /// <param name="actualLogin">Sets to false to avoid any login side-effect (such as updating the LastLoginTime) on success.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The login result.</returns>
        public async Task<LoginResult> LoginUserAsync( ISqlCallContext ctx, int userId, string password, bool actualLogin = true, CancellationToken cancellationToken = default( CancellationToken ) )
        {
            using( var c = new SqlCommand( _commandReadByUserId ) )
            {
                c.Parameters.AddWithValue( "@UserId", userId );
                return await DoVerifyAsync( ctx, c, password, userId, actualLogin, cancellationToken ).ConfigureAwait( false );
            }
        }

        /// <summary>
        /// Verifies a password for a user name.
        /// This automatically updates the hash if the <see cref="HashIterationCount"/> changed
        /// or if the internal algorithm is upgraded.
        /// </summary>
        /// <param name="ctx">The call context to use.</param>
        /// <param name="userName">The user name.</param>
        /// <param name="password">The password to challenge.</param>
        /// <param name="actualLogin">Sets to false to avoid any login side-effect (such as updating the LastLoginTime) on success.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The login result.</returns>
        public async Task<LoginResult> LoginUserAsync( ISqlCallContext ctx, string userName, string password, bool actualLogin = true, CancellationToken cancellationToken = default(CancellationToken) )
        {
            using( var c = CreateReadByNameCommand( userName ) )
            {
                return await DoVerifyAsync( ctx, c, password, userName, actualLogin, cancellationToken ).ConfigureAwait( false );
            }
        }

        /// <summary>
        /// Creates the command to read the user hash, last login time and identifier from its name.
        /// Defaults to: "select p.PwdHash, u.UserId, p.FailedAttemptCount from CK.tUser u left outer join CK.tUserPassword p on p.UserId = u.UserId where u.UserName=@UserName".
        /// By overriding this, what is considered as the login name (currently the tUser.UserName) can be changed. 
        /// </summary>
        /// <param name="userName">The user name to lookup.</param>
        /// <returns>The command that must select the hash and user identifier.</returns>
        protected virtual SqlCommand CreateReadByNameCommand( string userName )
        {
            var c = new SqlCommand( "select p.PwdHash, u.UserId, p.FailedAttemptCount from CK.tUser u left outer join CK.tUserPassword p on p.UserId = u.UserId where u.UserName=@UserName" );
            c.Parameters.AddWithValue( "@UserName", userName );
            return c;
        }

        async Task<LoginResult> DoVerifyAsync( ISqlCallContext ctx, SqlCommand hashReader, string password, object objectKey, bool actualLogin, CancellationToken cancellationToken )
        {
            if( string.IsNullOrEmpty( password ) ) return new LoginResult( KnownLoginFailureCode.InvalidCredentials );
            // 1 - Get the PwdHash, UserId and FailedAttemptCount.
            //     hash is null if the user is not a UserPassword: we'll try to migrate it.
            //     hash can be empty iif a previous attempt to migrate it has failed.

            var read = await ctx[Database].ExecuteSingleRowAsync( hashReader, row => row == null
                                    ? (0, null, -1)
                                    : (UserId: row.GetInt32( 1 ),
                                       PwdHash:
                                            row.IsDBNull( 0 ) ? null : row.GetBytes( 0 ),
                                       FailedAttemptCount:
                                            row.IsDBNull( 2 ) ? -1 : row.GetByte( 2 )), cancellationToken ).ConfigureAwait( false );
            if( read.UserId == 0 ) return new LoginResult( KnownLoginFailureCode.InvalidUserKey );

            PasswordVerificationResult result = PasswordVerificationResult.Failed;
            PasswordHasher p = null;
            IUserPasswordMigrator migrator = null;
            // 2 - Handle external password migration or check the hash.
            if( read.PwdHash == null || read.PwdHash.Length == 0 )
            {
                migrator = UserPasswordPackage.PasswordMigrator;
                if( migrator != null && migrator.VerifyPassword( ctx, read.UserId, password ) )
                {
                    result = PasswordVerificationResult.SuccessRehashNeeded;
                    p = new PasswordHasher( HashIterationCount );
                }
            }
            else
            {
                p = new PasswordHasher( HashIterationCount );
                result = p.VerifyHashedPassword( read.PwdHash, password );
            }
            // 3 - Handle result.
            var mode = actualLogin ? UCLMode.WithActualLogin : UCLMode.WithCheckLogin;
            if( result == PasswordVerificationResult.SuccessRehashNeeded )
            {
                // 3.1 - If migration occurred, create the user with its hashed password.
                //       Otherwise, rehash the password and update the database.
                mode |= UCLMode.CreateOrUpdate;
                UCLResult r = await PasswordUserUCLAsync( ctx, 1, read.UserId, p.HashPassword( password ), mode, null, cancellationToken ).ConfigureAwait( false );
                if( r.OperationResult != UCResult.None && migrator != null )
                {
                    migrator.MigrationDone( ctx, read.UserId );
                }
                return r.LoginResult;
            }
            if( result == PasswordVerificationResult.Failed && migrator != null )
            {
                // 3.2 - Migration failed, create the user with an empty hash.
                //       so that FailedAttemptCount (or other) can be handled.
                mode |= UCLMode.CreateOnly;
                mode &= ~UCLMode.UpdateOnly;
                // KnownLoginFailureCode is UnregisteredUser: the fact that we have a password migrator
                // (that failed to migrate) results in the user actually not registered in the UserPassword provider.
                UCLResult r = await PasswordUserUCLAsync( ctx, 1, read.UserId, Array.Empty<byte>(), mode, (int)KnownLoginFailureCode.UnregisteredUser, cancellationToken );
                return r.LoginResult;
            }
            // 4 - Challenges the database login checks.
            int? failureCode = null;
            if( result == PasswordVerificationResult.Failed ) failureCode = (int)KnownLoginFailureCode.InvalidCredentials;
            return (await PasswordUserUCLAsync( ctx, 1, read.UserId, null, mode, failureCode, cancellationToken )
                                    .ConfigureAwait( false )).LoginResult;
        }

        /// <summary>
        /// Destroys a PasswordUser for a user.
        /// </summary>
        /// <param name="ctx">The call context to use.</param>
        /// <param name="actorId">The acting actor identifier.</param>
        /// <param name="userId">The user identifier for which Password information must be destroyed.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The awaitable.</returns>
        [SqlProcedure( "sUserPasswordDestroy" )]
        public abstract Task DestroyPasswordUserAsync( ISqlCallContext ctx, int actorId, int userId, CancellationToken cancellationToken = default( CancellationToken ) );

        /// <summary>
        /// Low level stored procedure.
        /// This method should be used only if the standard password hasher and verification 
        /// mechanism is not used.
        /// </summary>
        /// <param name="ctx">The call context to use.</param>
        /// <param name="actorId">The acting actor identifier.</param>
        /// <param name="userId">The user identifier for wich a PassworUser must be created.</param>
        /// <param name="pwdHash">The initial raw hash (no more than 64 bytes).</param>
        /// <param name="mode">Configures Create, Update and/or WithLogin behaviors.</param>
        /// <param name="loginFailureCode">
        /// Login failure code (it is the <see cref="KnownLoginFailureCode.InvalidCredentials"/> when
        /// password match has failed).
        /// </param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The operation result.</returns>
        [SqlProcedure("sUserPasswordUCL")]
        protected abstract Task<UCLResult> PasswordUserUCLAsync( ISqlCallContext ctx, int actorId, int userId, byte[] pwdHash, UCLMode mode, int? loginFailureCode = null, CancellationToken cancellationToken = default( CancellationToken ) );

    }
}
