--BPM Database

CREATE TABLE [dbo].[Employee](
	[OID] [nchar](32) NOT NULL,
	[employeeId] [nvarchar](100) NOT NULL,
	[organizationOID] [nchar](32) NOT NULL,
	[userOID] [nchar](32) NOT NULL,
	[objectVersion] [int] NOT NULL,
	[validTo] [datetime] NULL,
 CONSTRAINT [PK_Employee] PRIMARY KEY CLUSTERED 
(
	[OID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]


CREATE TABLE [dbo].[Organization](
	[OID] [nchar](32) NOT NULL,
	[id] [nvarchar](100) NOT NULL,
	[objectVersion] [int] NOT NULL,
	[organizationName] [nvarchar](100) NOT NULL,
 CONSTRAINT [PK_Organization] PRIMARY KEY CLUSTERED 
(
	[OID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]


CREATE TABLE [dbo].[OrganizationUnit](
	[OID] [nchar](32) NOT NULL,
	[id] [nvarchar](100) NOT NULL,
	[organizationUnitName] [nvarchar](100) NOT NULL,
	[managerOID] [nchar](32) NULL,
	[superUnitOID] [nchar](32) NULL,
	[objectVersion] [int] NOT NULL,
	[organizationUnitType] [int] NOT NULL,
	[levelOID] [nchar](32) NULL,
	[organizationOID] [nchar](32) NOT NULL,
	[validType] [int] NOT NULL,
 CONSTRAINT [PK_OrganizationUnit] PRIMARY KEY CLUSTERED 
(
	[OID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]

CREATE TABLE [dbo].[OrganizationUnitLevel](
	[OID] [nchar](32) NOT NULL,
	[objectVersion] [int] NOT NULL,
	[levelValue] [int] NOT NULL,
	[organizationUnitLevelName] [nvarchar](100) NOT NULL,
	[organizationOID] [nchar](32) NOT NULL,
	[description] [ntext] NULL,
 CONSTRAINT [PK_OrganizationUnitLevel] PRIMARY KEY CLUSTERED 
(
	[OID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]



CREATE TABLE [dbo].[Users](
	[OID] [nchar](32) NOT NULL,
	[id] [nvarchar](100) NOT NULL,
	[userName] [nvarchar](100) NOT NULL,
	[objectVersion] [int] NOT NULL,
	[password] [nvarchar](50) NOT NULL,
	[leaveDate] [datetime] NULL,
	[referCalendarOID] [nchar](32) NULL,
	[identificationType] [nvarchar](50) NOT NULL,
	[mailAddress] [nvarchar](100) NULL,
	[localeString] [nvarchar](100) NOT NULL,
	[phoneNumber] [nvarchar](100) NULL,
	[workflowServerOID] [nchar](32) NULL,
	[enableSubstitute] [int] NOT NULL,
	[endSubstituteTime] [datetime] NULL,
	[startSubstituteTime] [datetime] NULL,
	[cost] [int] NULL,
	[mailingFrequencyType] [int] NOT NULL,
	[ldapid] [nvarchar](100) NULL,
	[intermissionDate] [datetime] NULL,
	[lastUptPwdDate] [datetime] NULL,
	[performForwardType] [int] NOT NULL,
	[userTaskDisplay] [int] NULL DEFAULT ((1)),
	[currentType] [int] NULL,
	[createdTime] [datetime] NULL,
	[passwordWrongTimes] [int] NULL,
	[traceWorkStatus] [int] NULL,
 CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED 
(
	[OID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]



--Oracle
ALTER TABLE YCS.GEM_FILE
 DROP PRIMARY KEY CASCADE;

DROP TABLE YCS.GEM_FILE CASCADE CONSTRAINTS;

CREATE TABLE YCS.GEM_FILE
(
  GEM01      VARCHAR2(10 BYTE),
  GEM02      VARCHAR2(80 BYTE),
  GEM03      VARCHAR2(80 BYTE),
  GEM04      VARCHAR2(6 BYTE),
  GEM05      VARCHAR2(1 BYTE),
  GEM06      VARCHAR2(1 BYTE),
  GEM07      VARCHAR2(1 BYTE),
  GEM08      VARCHAR2(1 BYTE),
  GEMACTI    VARCHAR2(1 BYTE),
  GEMUSER    VARCHAR2(10 BYTE),
  GEMGRUP    VARCHAR2(10 BYTE),
  GEMMODU    VARCHAR2(10 BYTE),
  GEMDATE    DATE,
  GEM09      VARCHAR2(1 BYTE),
  GEM10      VARCHAR2(10 BYTE),
  TA_GEM001  VARCHAR2(10 BYTE),
  TA_GEM002  VARCHAR2(10 BYTE),
  TA_GEM003  VARCHAR2(255 BYTE),
  TA_GEM004  VARCHAR2(255 BYTE),
  TA_GEM005  DATE,
  TA_GEM006  NUMBER(20,6),
  TA_GEM007  NUMBER(20,6)
)
TABLESPACE DBS1
PCTUSED    0
PCTFREE    10
INITRANS   1
MAXTRANS   255
STORAGE    (
            INITIAL          64K
            NEXT             1M
            MINEXTENTS       1
            MAXEXTENTS       UNLIMITED
            PCTINCREASE      0
            BUFFER_POOL      DEFAULT
           )
LOGGING 
NOCOMPRESS 
NOCACHE
NOPARALLEL
MONITORING;


CREATE UNIQUE INDEX YCS.GEM_PK ON YCS.GEM_FILE
(GEM01)
LOGGING
TABLESPACE DBS1
PCTFREE    10
INITRANS   2
MAXTRANS   255
STORAGE    (
            INITIAL          64K
            NEXT             1M
            MINEXTENTS       1
            MAXEXTENTS       UNLIMITED
            PCTINCREASE      0
            BUFFER_POOL      DEFAULT
           )
NOPARALLEL;


ALTER TABLE YCS.GEM_FILE ADD (
  CONSTRAINT GEM_PK
 PRIMARY KEY
 (GEM01)
    USING INDEX 
    TABLESPACE DBS1
    PCTFREE    10
    INITRANS   2
    MAXTRANS   255
    STORAGE    (
                INITIAL          64K
                NEXT             1M
                MINEXTENTS       1
                MAXEXTENTS       UNLIMITED
                PCTINCREASE      0
               ));

GRANT DELETE, INDEX, INSERT, SELECT, UPDATE ON YCS.GEM_FILE TO PUBLIC;


ALTER TABLE YCS.GEN_FILE
 DROP PRIMARY KEY CASCADE;

DROP TABLE YCS.GEN_FILE CASCADE CONSTRAINTS;

CREATE TABLE YCS.GEN_FILE
(
  GEN01     VARCHAR2(10 BYTE),
  GEN02     VARCHAR2(40 BYTE),
  GEN03     VARCHAR2(10 BYTE),
  GEN04     VARCHAR2(80 BYTE),
  GEN05     VARCHAR2(5 BYTE),
  GEN06     VARCHAR2(80 BYTE),
  GENACTI   VARCHAR2(1 BYTE),
  GENUSER   VARCHAR2(10 BYTE),
  GENGRUP   VARCHAR2(10 BYTE),
  GENMODU   VARCHAR2(10 BYTE),
  GENDATE   DATE,
  TA_GEN07  VARCHAR2(10 BYTE),
  TA_GEN08  VARCHAR2(30 BYTE)
)
TABLESPACE DBS1
PCTUSED    0
PCTFREE    10
INITRANS   1
MAXTRANS   255
STORAGE    (
            INITIAL          64K
            NEXT             1M
            MINEXTENTS       1
            MAXEXTENTS       UNLIMITED
            PCTINCREASE      0
            BUFFER_POOL      DEFAULT
           )
LOGGING 
NOCOMPRESS 
NOCACHE
NOPARALLEL
MONITORING;


CREATE UNIQUE INDEX YCS.GEN_PK ON YCS.GEN_FILE
(GEN01)
LOGGING
TABLESPACE DBS1
PCTFREE    10
INITRANS   2
MAXTRANS   255
STORAGE    (
            INITIAL          64K
            NEXT             1M
            MINEXTENTS       1
            MAXEXTENTS       UNLIMITED
            PCTINCREASE      0
            BUFFER_POOL      DEFAULT
           )
NOPARALLEL;


ALTER TABLE YCS.GEN_FILE ADD (
  CONSTRAINT GEN_PK
 PRIMARY KEY
 (GEN01)
    USING INDEX 
    TABLESPACE DBS1
    PCTFREE    10
    INITRANS   2
    MAXTRANS   255
    STORAGE    (
                INITIAL          64K
                NEXT             1M
                MINEXTENTS       1
                MAXEXTENTS       UNLIMITED
                PCTINCREASE      0
               ));

GRANT DELETE, INDEX, INSERT, SELECT, UPDATE ON YCS.GEN_FILE TO PUBLIC;