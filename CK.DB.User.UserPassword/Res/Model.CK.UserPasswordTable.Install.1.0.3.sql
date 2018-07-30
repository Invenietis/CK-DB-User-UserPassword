--[beginscript]

create table CK.tUserPassword
(
	UserId int not null,
	PwdHash varbinary(64) not null,
	LastWriteTime datetime2(2) not null,
	LastLoginTime datetime2(2) not null,
    FailedAttemptCount tinyint not null constraint DF_CK_UserPassword default(0),
	constraint PK_CK_UserPassword primary key (UserId),
	constraint FK_CK_UserPassword_UserId foreign key (UserId) references CK.tUser(UserId)
);

insert into CK.tUserPassword( UserId, PwdHash, LastWriteTime, LastLoginTime ) values(0, 0x, sysutcdatetime(), sysutcdatetime() );

--[endscript]
