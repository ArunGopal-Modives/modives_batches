INSERT INTO `modives-analytics`.billing_transactions
(
    Id,
	AppId,
	F_VerificationDate,
	F_VerificationDateMonthYear,
	LookupActive,
	Status,
	Lob,
	Expedite,
	OverallVerificationType,
	F_VerificationType,
	ClientName,
	ClientId,
	GroupLocationName,
	GroupLocationID,
	LocationName,
	LocationId,
	DealerName,
	DealerId,
	DCID,
	DealerShipType,
	Name,
	Email,
	MobileNumber,
	DealId,
	Vin,
	CPI,
	TransactionType,
	Carrier,
	MethodSent,
	OriginationSource,
	DateModified,
	LeaseStartDate,
	LeaseEndDate,
	VerificationDate,
	IntegrationPartner,
	IsDeleted,
	ApplicantLicenseStatus,
	ActiveStatus,
	PolicyStatus,
	AdequacyStatus,
	F_Status,
	CheckAnyTime,
	quote_PolicyId,
	quote_QuoteId,
	looker_policy_date,
	looker_quote_date,
	converted,
	UnitName,
	verificationSource,
	LEMONADE_PolicyId,
	LEMONADE_QuoteId,
	PolicyCreateDateTime,
	PolicyCreationMonth,
    ContractName,
    PartnerID,
    OverallVerificationAndMonitoring,
    F_VIN,
    F_VerificationType_Updated,
    F_ApplicantLicenseStatus,
    load_date
)
WITH base_data AS (
SELECT DISTINCT
            v.id, 
            pvr.Id AS AppId,
            CASE 
				WHEN v.Status = 'Authorizing' AND pvr.LastUpdatedTs IS NOT NULL THEN pvr.LastUpdatedTs
				WHEN v.Status = 'Link Sent' AND pvr.createdDate IS NOT NULL THEN pvr.createdDate
                ELSE v.TimeStamp
			END AS combinedDate,
            CASE 
				WHEN p.IsTestDealer = 1 THEN 'Test Dealer'
				WHEN (IFNULL(p.isDeleted,0) = 0 AND IFNULL(p.IsTestDealer,0) = 0) THEN 'Yes'
				WHEN (IFNULL(p.isDeleted,0) = 1 AND IFNULL(p.IsTestDealer,0) = 0) THEN 'No'
				ELSE NULL
			END AS LookupActive,
            v.Status,
            CASE
				WHEN p.clientid = '5bf88abf-600a-4bed-8c86-d508ab6d36e0' THEN 'Dealer Center'
				WHEN p.clientid = '6ef6df80-dfc5-11ef-a6a4-00224831d19f' THEN 'Dealer Center'
				WHEN p.clientid = '278b2033-7286-46ba-85dc-89245302e2f0' THEN 'Connextions'
				WHEN whs.DisplayName = 'AutoCenter' THEN 'Dealer Center'
                WHEN whs.DisplayName = 'ExternalClient20' THEN 'MBurse'
                
				WHEN (
						IFNULL(whs.DisplayName, '') = '' OR 
                        whs.DisplayName IN ('VMR Web Application', 'VMR Service', 'Console Test / Angular Application', 'Swagger Application', 'Client_App', 'Test_Client', 'ModivesSharedServices','Modives', 'Homes') 
					 ) THEN 'Modives'
                ELSE whs.DisplayName 
			 END AS clientName,
             CASE 
				WHEN avr.Lob = 1 THEN 'PROPERTY' 
                ELSE 'AUTO' 
			END AS Lob, 
			v.Expedite,
            CASE
                WHEN (v.VerificationType = 1 OR v.VerificationType = 2) THEN 'Automatic'
                WHEN (v.VerificationType = 0 OR v.VerificationType = 3) THEN 'Manual'
                ELSE NULL
			 END AS OverallVerificationType,
			CASE
				WHEN IFNULL(v.ParentVerificationId, '') = '' THEN
					CASE v.VerificationType
						WHEN '0' THEN 'Manual Verification'
						WHEN '3' THEN 'Manual Verification'
						WHEN '1' THEN 'Digital Verification'
						WHEN '2' THEN 'Digital Verification'
					END
				WHEN v.ParentVerificationId IS NOT NULL THEN
					CASE v.VerificationType
						WHEN '0' THEN 'Manual Monitoring'
						WHEN '3' THEN 'Manual Monitoring'
						WHEN '1' THEN 'Digital Monitoring'
						WHEN '2' THEN 'Digital Monitoring'
					END
				ELSE NULL
			END AS base_verification_type,
			CASE 
				WHEN (ll.Email IS NOT NULL or ll.`Policy ID - Encrypted` is not null) AND pb.PolicyId IS NOT NULL THEN 'Lemonade'
				WHEN (v.VerificationType = 1 OR v.VerificationType = 2) 
				THEN 
					CASE 
						WHEN v.Status IN ('Link Sent','Authorizing') THEN v.Status
						WHEN v.Status IN ('Unverified','Verified','Verifying','Manual Link Sent','Failed','Incomplete','Resumed','Canceled','Cancelled')
						THEN 
							CASE 
								WHEN pb.PolicyId IS NOT NULL THEN 'Lemonade'
                                WHEN IFNULL(pb.PolicyId, '') = '' AND v.IntegrationPartner IS NOT NULL THEN v.IntegrationPartner
								ELSE NULL
							END
						WHEN v.Status = 'Link Sent' THEN NULL
                        WHEN v.Status = 'Pending Manual Verification' AND v.IntegrationPartner = 'Canopy' THEN NULL
						ELSE NULL
					END
                    
				WHEN (v.VerificationType = 0 OR v.VerificationType = 3) #Manual
					AND v.Status IN ('Cancelled', 'Failed','Link Sent','Pending Manual Verification','Unverified','Verified',
									'Verifying','Manual Link Sent')
				THEN  
					CASE 
						WHEN pb.PolicyId IS NOT NULL THEN 'Lemonade'
                        WHEN sm.ContactMethod = 1 THEN 'Phone' 
						WHEN sm.ContactMethod = 2 THEN 'Portal' 
						WHEN sm.ContactMethod = 3 THEN 'Email' 
                        ELSE 'Phone'
					END
				ELSE NULL
			END AS combinedStatus,
            loc.Name AS HQName,
            avr.clientId,
            loc.Id AS LocationId,
            p.PropertyName AS OfficeName,
            avr.ApplicantName AS `Name`,
            IFNULL(v.Email, avr.applicantEmail) AS Email,
            v.MobileNumber,
            p.Id as DealerId,
            CASE p.DealerShipType 
				WHEN 1 THEN 'BHPH'
				WHEN 2 THEN 'Franchise'
				WHEN 3 THEN 'Rental'
				WHEN 4 THEN 'Independent'
				WHEN 5 THEN 'Property'
				ELSE 'Dealership Not Found'
			END AS DealerShipType,
            per.LoginId AS Dcid,
            p.PropertyName AS Dealership,
            pvr.dealId,
            vfagg.Vin,
            CASE WHEN pvr.IsNewVINPolicy = 1 THEN 'Yes' ELSE 'No' END AS NewVin,
            CASE WHEN pvr.isCPI = 1 THEN 'Yes' ELSE 'No' END AS CPI,
            act.TypeCode AS TransactionType,
            avr.Carrier,
            CASE avr.sentMethod
				WHEN 1 THEN 'Save'
				WHEN 2 THEN 'Email'
				WHEN 3 THEN 'SMS'
				WHEN 4 THEN 'Email & SMS'
				ELSE 'Other'
			END AS methodSent,
            IFNULL(v.VerificationOrigination, avr.VerificationOrigination) AS OriginationSource,
            pvr.LastUpdatedTs AS dateModified,
			v.LeaseStartDate,
            v.LeaseEndDate,
            v.TimeStamp AS VerificationDate,
            v.IntegrationPartner,
            (
				CASE 
					WHEN (IFNULL(avr.isDeleted, 0) = 1 OR IFNULL(v.isDeleted,0) = 1) THEN 1 
                    ELSE  avr.isDeleted 
				END
			) AS isDeleted,
            avr.PolicyStatus AS activeStatus,
            vlr.OriginalPolicyStatus AS policyStatus,
            CASE 
				WHEN wfs.`Name` = 'Pending' THEN 'Link Not Sent'
				WHEN IFNULL(pvr.isCPI, 0) = 1 THEN 'CPI'
				WHEN wfs.`Name` = 'Sign up Completed' THEN 'Pending Review'
				WHEN wfs.StatusId = 2 AND avr.ModivesVerificationId IS NOT NULL THEN 'Authorizing'
				WHEN wfs.`Name` = 'Pending Manual Verification' THEN 'Pending Review'
				WHEN wfs.`Name` = 'Verified' THEN 'Adequate'
				WHEN wfs.`Name` = 'Unverified' THEN 'Inadequate'
				ELSE wfs.`Name`
			END AS AdequacyStatus,
            avr.LicenseStatus AS ApplicantLicenseStatus,
            CASE mnr.Status 
				WHEN 0 THEN 'Pending'
                WHEN 1 THEN 'Completed'
                WHEN 2 THEN 'Failed'
                ELSE NULL
			END AS CheckAnyTime,
            pb.PolicyId AS quote_PolicyId,
            pb.QuoteId AS quote_QuoteId,
            pb.LastUpdated AS looker_policy_date,
            qt.CreateDateTime AS looker_quote_date,
            IFNULL(v.verificationSource, avr.verificationSource) AS verificationSource,
            v.SmrRenterVerificationId,
			p.IsTestDealer

		FROM `modives-analytics`.stg_verifications v
        LEFT JOIN `modives-analytics`.stg_applicantverificationreqdetail avr ON  avr.ApplicantVerificationReqGuidId = v.SmrRenterVerificationId
        INNER JOIN `modives-analytics`.stg_propertyverificationrequest pvr ON avr.VerificationReqId = pvr.Id
        INNER JOIN `modives-analytics`.stg_property p ON p.Id = pvr.PropertyId
        INNER JOIN `modives-analytics`.stg_locationperson l ON l.LocationId = p.LocationId
        LEFT JOIN `modives-analytics`.stg_person per ON l.personid = per.id AND per.clientId = avr.clientId AND per.loginId NOT LIKE '%@%'
        INNER JOIN `modives-analytics`.stg_location loc ON l.LocationId = loc.Id
        LEFT JOIN `modives-analytics`.stg_acquisitiontype act ON act.Id = pvr.AcquisitionTypeId
        LEFT JOIN `modives-analytics`.stg_supportmonitoring sm ON sm.VerificationId = v.id
        LEFT JOIN `modives-analytics`.stg_verificationlogicresults vlr ON vlr.VerificationId = v.Id
		LEFT JOIN `modives-analytics`.stg_quotetransactions qt ON qt.VerificationId = avr.ApplicantVerificationReqGuidId
        LEFT JOIN `modives-analytics`.stg_policybind pb on qt.TransactionId = pb.TransactionId and pb.PolicyId is not null
		LEFT JOIN `modives-analytics`.stg_openiddictapplications whs ON whs.clientId = p.clientId
        LEFT JOIN `modives-analytics`.stg_looker_inputdata ll ON  (ll.Email = v.Email or ll.`Policy ID - Encrypted` = pb.PolicyId)
        LEFT JOIN `modives-analytics`.stg_manualmonitoringrequests mnr ON mnr.AppId = pvr.Id and mnr.ApplicantVerificationId = v.SmrRenterVerificationId 
	    LEFT JOIN (
			SELECT
				VerificationLogicResultId,
				VerificationValue AS VIN
			FROM (
				SELECT
					vfr.VerificationLogicResultId,
					IFNULL(NULLIF(vfr.VerificationValue,''),vfr.InsuranceValue) AS VerificationValue,
					ROW_NUMBER() OVER (
						PARTITION BY vfr.VerificationLogicResultId
						ORDER BY vfr.LastModificationTime DESC
					) AS rn
				FROM `modives-analytics`.stg_verificationfieldresults vfr
				WHERE vfr.InsuranceField = 'VIN' AND (IFNULL(NULLIF(vfr.VerificationValue,''),'')<> '' OR IFNULL(NULLIF(vfr.InsuranceValue,''),'')<> '')
			) t
			WHERE rn = 1
		) vfagg 
		ON vlr.Id = vfagg.VerificationLogicResultId
        LEFT JOIN (
			SELECT DISTINCT * FROM (
					SELECT 
						ROW_NUMBER() OVER(
							PARTITION BY pvr.id
							ORDER BY wrs.LastUpdatedTs DESC
							) AS rNo,
							wrs.*
						FROM `modives-analytics`.stg_workflowrequests wrs
						INNER JOIN `modives-analytics`.stg_applicantverificationreqdetail avr
							ON avr.Id = wrs.ObjectId
						   AND avr.IsDeleted <> 1
						INNER JOIN `modives-analytics`.stg_propertyverificationrequest pvr
							ON pvr.Id = avr.VerificationReqId
					) AS ranked
					WHERE rNo = 1
		 ) AS wr ON avr.Id = wr.ObjectId AND rNo = 1
		LEFT JOIN `modives-analytics`.stg_workflowfunctionstatuses wfs
			ON wfs.StatusId = wr.WorkFlowStatusId
		   AND wr.WorkFlowFunctionId = wfs.WorkFlowId
		   AND wfs.IsDeleted <> 1
    LEFT JOIN `modives-analytics`.stg_PropertyUnitDetail pd ON pd.Id = pvr.UnitId
	WHERE DATE(v.TimeStamp) >= DATE_SUB(CURDATE(), INTERVAL @BillingRefreshDays DAY)
    ),
    
    UnitCTE AS (
    SELECT DISTINCT
        pvr.Id AS AppId,
        pd.UnitName
    FROM `modives-analytics`.stg_propertyverificationrequest pvr
    JOIN `modives-analytics`.stg_PropertyUnitDetail pd 
        ON pd.Id = pvr.UnitId
    WHERE pd.UnitName IS NOT NULL

),
transaction_data AS (
	SELECT 
		qt.TransactionId,
        qt.VerificationId,
		p.PolicyId as LEMONADE_PolicyId,
        p.QuoteId AS LEMONADE_QuoteId,
		qt.CreateDateTime AS PolicyCreateDateTime,
		DATE_FORMAT(DATE(qt.CreateDateTime), '%m-%Y')  AS PolicyCreationMonth
	FROM `modives-analytics`.stg_quotetransactions qt 
	LEFT join `modives-analytics`.stg_quoterequest qtr ON qt.TransactionId =qtr.TransactionId
	LEFT join `modives-analytics`.stg_lead l ON qtr.leadid = l.id
	LEFT join `modives-analytics`.stg_renters r ON qt.TransactionId = r.TransactionId
	LEFT join `modives-analytics`.stg_policybind p ON qt.TransactionId = p.TransactionId
	WHERE qt.ProviderName = 'LEMONADE' and p.policyid is not null
),
derived_data AS (
    SELECT DISTINCT
        b.Id,
		b.AppId, 
		DATE(b.combinedDate) AS F_VerificationDate,
		DATE_FORMAT(DATE(b.combinedDate), '%m-%Y') AS F_VerificationDateMonthYear,
		b.LookupActive,
		b.Status,
		b.Lob,
		b.Expedite,
        CASE 
			WHEN td.TransactionId IS NOT NULL THEN 'APILemonadeActual'
			WHEN b.combinedStatus = 'Lemonade' THEN 'APILemonade'
            WHEN b.Status IN ('Link Sent','Authorizing') THEN 'Campaigning'
            ELSE b.OverallVerificationType
		END AS OverallVerificationType,
        CASE
			WHEN b.Status IN ('Link Sent','Authorizing') THEN b.Status
			WHEN (OverallVerificationType = 'Manual' AND base_verification_type = 'Manual Verification')
				THEN
					CASE 
						WHEN combinedStatus = 'Lemonade' THEN 'Digital Verification'
						ELSE base_verification_type
					END
			WHEN OverallVerificationType = 'Manual' AND base_verification_type = 'Manual Monitoring'
				THEN
					CASE
						WHEN combinedStatus = 'Lemonade' THEN 'Digital Monitoring'
						ELSE base_verification_type
					END
			ELSE base_verification_type
		END AS F_VerificationType,
        b.HQName AS LocationName,
		b.ClientId,
		b.ClientName,
		b.LocationId,
		b.OfficeName AS DealerName,
		b.`Name`,
		b.Email,
		b.MobileNumber,
		b.DealerId,
		b.DealerShipType,
		b.Dcid AS DCID,
		b.Dealership,
		b.DealId,
		b.Vin,
		b.CPI,
		b.TransactionType,
		b.Carrier,
		b.MethodSent,
        b.OriginationSource,
		b.DateModified,
		b.LeaseStartDate,
		b.LeaseEndDate,
		b.VerificationDate,
		b.IntegrationPartner,
		b.IsDeleted,
		b.ApplicantLicenseStatus,
		b.ActiveStatus,
		b.PolicyStatus,
		b.AdequacyStatus,
		b.CombinedStatus AS F_Status,
		b.CheckAnyTime,
        b.quote_PolicyId,
		b.quote_QuoteId,
        b.looker_policy_date,
        b.looker_quote_date,
        b.verificationSource,
		td.LEMONADE_PolicyId,
        td.LEMONADE_QuoteId,
        td.PolicyCreateDateTime,
        td.PolicyCreationMonth,
        CASE
			WHEN b.IsTestDealer = 1 THEN 'Test'
			WHEN IFNULL(b.ClientName, '') NOT LIKE '%Modives%'
				 AND IFNULL(DC.ContractName, '') NOT LIKE '%Test%'
				 AND IFNULL(DC.ContractName, '') NOT LIKE '%Venn%'
			THEN b.ClientName

			WHEN b.ClientName LIKE '%Modives%'
				 AND IFNULL(DC.ContractName, '') NOT LIKE '%Test%'
				 AND IFNULL(DC.ContractName, '') NOT LIKE '%Venn%'
			THEN b.HQName

			ELSE DC.ContractName  
		END AS ContractName,
		CASE 
			WHEN IFNULL(b.Dcid,'')<>'' THEN b.Dcid
			ELSE b.DealerId 
		END AS PartnerID,
		CASE 
			WHEN b.base_verification_type IN ('Manual Verification','Digital Verification') THEN 'Verification'
			WHEN b.base_verification_type IN ('Manual Monitoring','Digital Monitoring') THEN 'Monitoring'
		END AS OverallVerificationAndMonitoring,
		CASE
			WHEN b.Vin IS NOT NULL THEN 'VIN-Yes'
			ELSE 'VIN-No'
		END AS F_VIN,
		CASE
			WHEN vti.FlowType = 1 THEN 'Digital Verification'
			WHEN vti.FlowType = 2 THEN 'Digital Monitoring'
			ELSE b.base_verification_type
		END AS F_VerificationType_Updated,
		CASE
			WHEN b.ApplicantLicenseStatus = 'Skipped' or b.ApplicantLicenseStatus is null or b.ApplicantLicenseStatus = '' THEN 'License_No'
			WHEN b.ApplicantLicenseStatus IS NOT NULL OR b.ApplicantLicenseStatus <> '' THEN 'License_Yes'
		END AS F_ApplicantLicenseStatus
    FROM base_data b
    LEFT JOIN transaction_data td ON td.VerificationId = b.SmrRenterVerificationId
    LEFT JOIN `modives-analytics`.Dealer_Contracts DC ON b.DealerId = DC.DealerId
	LEFT JOIN `modives-analytics`.stg_verificationtriggerinput vti 
			ON vti.VerificationReqId = b.AppId 
			AND DATE(vti.PickedAt) = DATE(b.VerificationDate)

),
deduped_data AS (
    SELECT DISTINCT
        d.*,
        ROW_NUMBER() OVER (
            PARTITION BY 
				d.Id,
                d.AppId,
				d.LocationId,
                d.DealerId
            ORDER BY 
                CASE 
                    WHEN d.ClientId = 'VMR_App' THEN 1
                    ELSE 2
                END
        ) AS rn
    FROM derived_data d
),
quote_data_base AS (
SELECT DISTINCT
	v.id, 
	pvr.Id AS AppId,
    IFNULL(pb.LastUpdated, qt.CreateDateTime) AS CombinedDate,
    CASE 
		WHEN p.IsTestDealer = 1 THEN 'Test Dealer'
		WHEN (IFNULL(p.isDeleted,0) = 0 AND IFNULL(p.IsTestDealer,0) = 0) THEN 'Yes'
		WHEN (IFNULL(p.isDeleted,0) = 1 AND IFNULL(p.IsTestDealer,0) = 0) THEN 'No'
		ELSE NULL
	END AS LookupActive,
	v.Status,
    CASE
		WHEN whs.DisplayName = 'AutoCenter' THEN 'Dealer Center'
		WHEN whs.DisplayName = 'ExternalClient20' THEN 'MBurse'
		WHEN (
				IFNULL(whs.DisplayName, '') = '' OR 
				whs.DisplayName IN ('VMR Web Application', 'VMR Service', 'Console Test / Angular Application', 'Swagger Application', 'Client_App', 'Test_Client', 'ModivesSharedServices','Modives', 'Homes') 
			 ) THEN 'Modives'
		ELSE whs.DisplayName 
	END AS clientName,
    CASE 
		WHEN avr.Lob = 1 THEN 'PROPERTY' 
        ELSE 'AUTO' 
	END AS Lob, 
	v.Expedite,
    CASE
      WHEN (v.VerificationType = 1 OR v.VerificationType = 2) THEN 'Automatic'
      WHEN (v.VerificationType = 0 OR v.VerificationType = 3) THEN 'Manual'
      ELSE NULL
	END AS OverallVerificationType,
	CASE
		WHEN IFNULL(v.ParentVerificationId, '') = '' THEN
		CASE v.VerificationType
			WHEN '0' THEN 'Manual Verification'
			WHEN '3' THEN 'Manual Verification'
			WHEN '1' THEN 'Digital Verification'
			WHEN '2' THEN 'Digital Verification'
		END
		WHEN v.ParentVerificationId IS NOT NULL THEN
			CASE v.VerificationType
				WHEN '0' THEN 'Manual Monitoring'
				WHEN '3' THEN 'Manual Monitoring'
				WHEN '1' THEN 'Digital Monitoring'
				WHEN '2' THEN 'Digital Monitoring'
			END
		ELSE NULL
	END AS base_verification_type,
	CASE 
		WHEN (ll.Email IS NOT NULL or ll.`Policy ID - Encrypted` is not null) AND pb.PolicyId IS NOT NULL THEN 'Lemonade'
		WHEN (v.VerificationType = 1 OR v.VerificationType = 2) 
		THEN 
			CASE 
				WHEN v.Status IN ('Link Sent','Authorizing') THEN v.Status
				WHEN v.Status IN ('Unverified','Verified','Verifying','Manual Link Sent','Failed','Incomplete','Resumed','Canceled','Cancelled')
				THEN 
					CASE 
						WHEN pb.PolicyId IS NOT NULL THEN 'Lemonade'
                        WHEN IFNULL(pb.PolicyId, '') = '' AND v.IntegrationPartner IS NOT NULL THEN v.IntegrationPartner
						ELSE NULL
					END
						WHEN v.Status = 'Link Sent' THEN NULL
				WHEN v.Status = 'Pending Manual Verification' AND v.IntegrationPartner = 'Canopy' THEN NULL
				ELSE NULL
			END
                    
		WHEN (v.VerificationType = 0 OR v.VerificationType = 3) #Manual
					AND v.Status IN ('Cancelled', 'Failed','Link Sent','Pending Manual Verification','Unverified','Verified',
									'Verifying','Manual Link Sent')
				THEN  
					CASE 
						WHEN pb.PolicyId IS NOT NULL THEN 'Lemonade'
                        WHEN sm.ContactMethod = 1 THEN 'Phone' 
						WHEN sm.ContactMethod = 2 THEN 'Portal' 
						WHEN sm.ContactMethod = 3 THEN 'Email' 
                        ELSE 'Phone'
					END
		ELSE NULL
	END AS combinedStatus,
	loc.Id AS LocationId,
    loc.Name AS LocationName,
    IFNULL(whs.clientId,'') AS clientId,
    p.PropertyName AS DealerName,
	CONCAT(l.FirstName, '' ,l.LastName) AS `Name`,
    l.Email AS Email,
    l.Phone AS MobileNumber,
    p.Id as DealerId,
    CASE p.DealerShipType 
		WHEN 1 THEN 'BHPH'
		WHEN 2 THEN 'Franchise'
		WHEN 3 THEN 'Rental'
		WHEN 4 THEN 'Independent'
		WHEN 5 THEN 'Property'
		ELSE 'Dealership Not Found'
	END AS DealerShipType,
	per.LoginId AS Dcid,
    p.PropertyName AS Dealership,
    pvr.dealId,
	vfagg.Vin,
	CASE WHEN pvr.IsNewVINPolicy = 1 THEN 'Yes' ELSE 'No' END AS NewVin,
    CASE WHEN pvr.isCPI = 1 THEN 'Yes' ELSE 'No' END AS CPI,
    act.TypeCode AS TransactionType,
    avr.Carrier,
    CASE avr.sentMethod
		WHEN 1 THEN 'Save'
		WHEN 2 THEN 'Email'
		WHEN 3 THEN 'SMS'
		WHEN 4 THEN 'Email & SMS'
		ELSE 'Other'
	END AS methodSent,
    IFNULL(v.VerificationOrigination, avr.VerificationOrigination) AS OriginationSource,
    pvr.LastUpdatedTs AS dateModified,
	v.LeaseStartDate,
	v.LeaseEndDate,
	v.TimeStamp AS VerificationDate,
    v.IntegrationPartner,
	(
		CASE 
			WHEN (IFNULL(avr.isDeleted, 0) = 1 OR IFNULL(v.isDeleted,0) = 1) THEN 1 
            ELSE  avr.isDeleted 
		END
	) AS isDeleted,
    avr.PolicyStatus AS activeStatus,
	pb.PolicyStatus,
    CASE 
		WHEN wfs.`Name` = 'Pending' THEN 'Link Not Sent'
		WHEN IFNULL(pvr.isCPI, 0) = 1 THEN 'CPI'
		WHEN wfs.`Name` = 'Sign up Completed' THEN 'Pending Review'
		WHEN wfs.StatusId = 2 AND avr.ModivesVerificationId IS NOT NULL THEN 'Authorizing'
		WHEN wfs.`Name` = 'Pending Manual Verification' THEN 'Pending Review'
		WHEN wfs.`Name` = 'Verified' THEN 'Adequate'
		WHEN wfs.`Name` = 'Unverified' THEN 'Inadequate'
		ELSE wfs.`Name`
	END AS AdequacyStatus,
    avr.LicenseStatus AS ApplicantLicenseStatus,
	pb.PolicyId AS quote_PolicyId,
	pb.QuoteId AS quote_QuoteId,
	pb.LastUpdated AS looker_policy_date,
    qt.CreateDateTime AS looker_quote_date,    
    IFNULL(locpar.Name,'')  AS GroupLocationName,
    locpar.Id  AS GroupLocationID,
    IFNULL(v.verificationSource, avr.verificationSource) AS verificationSource,
    v.SmrRenterVerificationId,
    p.IsTestDealer
	
FROM `modives-analytics`.stg_quotetransactions qt
LEFT JOIN `modives-analytics`.stg_policybind pb ON qt.TransactionId = pb.TransactionId
LEFT JOIN `modives-analytics`.stg_quoterequest qtr ON qt.TransactionId =qtr.TransactionId
LEFT JOIN `modives-analytics`.stg_lead l ON qtr.leadid = l.id
INNER JOIN (
    SELECT *
    FROM (
        SELECT
            avr.*,
            ROW_NUMBER() OVER (
                PARTITION BY avr.applicantEmail
                ORDER BY avr.LastUpdatedTs DESC
            ) AS rn
        FROM `modives-analytics`.stg_applicantverificationreqdetail avr
    )t
    WHERE rn = 1
) avr
ON avr.applicantEmail = l.Email
INNER JOIN `modives-analytics`.stg_propertyverificationrequest pvr ON avr.VerificationReqId = pvr.Id
INNER JOIN `modives-analytics`.stg_property p ON p.Id = pvr.PropertyId
INNER JOIN `modives-analytics`.stg_location loc ON p.LocationId = loc.Id
LEFT JOIN `modives-analytics`.stg_verifications v ON avr.ApplicantVerificationReqGuidId = v.SmrRenterVerificationId
LEFT JOIN (
    SELECT hd1.*
    FROM `modives-analytics`.stg_hierarchydetails hd1
    WHERE hd1.ObjectType in (1,2) and
       EXISTS (
          SELECT 1
          FROM `modives-analytics`.stg_hierarchydetails hd2
          WHERE hd2.ObjectId = hd1.ParentObjectId
            AND hd2.IsHierarchyMaster = 1
      )
      AND NOT EXISTS (
          SELECT 1
          FROM `modives-analytics`.stg_hierarchydetails hd3
          WHERE hd3.ObjectId = hd1.ObjectId
            AND hd3.IsHierarchyMaster = 1
      )
) hd
    ON hd.ObjectId = loc.Id
LEFT JOIN `modives-analytics`.stg_location locpar
    ON locpar.Id = hd.ParentObjectId
LEFT JOIN `modives-analytics`.stg_openiddictapplications whs ON whs.clientId = avr.clientId
LEFT JOIN `modives-analytics`.stg_looker_inputdata ll ON  (ll.Email = v.Email or ll.`Policy ID - Encrypted` = pb.PolicyId)
LEFT JOIN `modives-analytics`.stg_supportmonitoring sm ON sm.VerificationId = v.id
LEFT JOIN `modives-analytics`.stg_locationperson locp ON locp.LocationId = p.LocationId
LEFT JOIN `modives-analytics`.stg_person per ON locp.personid = per.id AND per.clientId = avr.clientId AND per.loginId NOT LIKE '%@%'
LEFT JOIN `modives-analytics`.stg_verificationlogicresults vlr ON vlr.VerificationId = v.Id
LEFT JOIN (
			SELECT
				VerificationLogicResultId,
				VerificationValue AS VIN
			FROM (
				SELECT
					vfr.VerificationLogicResultId,
					IFNULL(NULLIF(vfr.VerificationValue,''),vfr.InsuranceValue) AS VerificationValue,
					ROW_NUMBER() OVER (
						PARTITION BY vfr.VerificationLogicResultId
						ORDER BY vfr.LastModificationTime DESC
					) AS rn
				FROM `modives-analytics`.stg_verificationfieldresults vfr
				WHERE vfr.InsuranceField = 'VIN' AND (IFNULL(NULLIF(vfr.VerificationValue,''),'')<> '' OR IFNULL(NULLIF(vfr.InsuranceValue,''),'')<> '')
			) t
			WHERE rn = 1
		) vfagg 
		ON vlr.Id = vfagg.VerificationLogicResultId
	LEFT JOIN `modives-analytics`.stg_acquisitiontype act ON act.Id = pvr.AcquisitionTypeId
    LEFT JOIN (
			SELECT DISTINCT * FROM (
					SELECT 
						ROW_NUMBER() OVER(
							PARTITION BY pvr.id
							ORDER BY wrs.LastUpdatedTs DESC
							) AS rNo,
							wrs.*
						FROM `modives-analytics`.stg_workflowrequests wrs
						INNER JOIN `modives-analytics`.stg_applicantverificationreqdetail avr
							ON avr.Id = wrs.ObjectId
						   AND avr.IsDeleted <> 1
						INNER JOIN `modives-analytics`.stg_propertyverificationrequest pvr
							ON pvr.Id = avr.VerificationReqId
					) AS ranked
					WHERE rNo = 1
		 ) AS wr ON avr.Id = wr.ObjectId AND rNo = 1
		LEFT JOIN `modives-analytics`.stg_workflowfunctionstatuses wfs
			ON wfs.StatusId = wr.WorkFlowStatusId
		   AND wr.WorkFlowFunctionId = wfs.WorkFlowId
		   AND wfs.IsDeleted <> 1
WHERE qt.ProviderName = 'LEMONADE'
AND DATE(pb.LastUpdated) >= DATE_SUB(CURDATE(), INTERVAL @BillingRefreshDays DAY)
AND NOT EXISTS (SELECT 1 FROM deduped_data dd WHERE dd.quote_PolicyId = pb.PolicyId AND dd.rn = 1)

),
quote_data AS (
SELECT DISTINCT
	q.id, 
	q.AppId,
    q.CombinedDate,
    DATE_FORMAT(DATE(q.combinedDate), '%m-%Y') AS F_VerificationDateMonthYear,
    q.LookupActive,
	q.Status,
    q.clientName,
    q.Lob, 
	q.Expedite,
    CASE 
		WHEN td.TransactionId IS NOT NULL THEN 'APILemonadeActual'
		WHEN q.combinedStatus = 'Lemonade' THEN 'APILemonade'
        WHEN q.Status IN ('Link Sent','Authorizing') THEN 'Campaigning'
        ELSE q.OverallVerificationType
	END AS OverallVerificationType,
	q.base_verification_type,
    CASE
			WHEN q.Status IN ('Link Sent','Authorizing') THEN q.Status
			WHEN (OverallVerificationType = 'Manual' AND base_verification_type = 'Manual Verification')
				THEN
					CASE
						WHEN combinedStatus = 'Lemonade' THEN 'Digital Verification'
						ELSE base_verification_type
					END
			WHEN OverallVerificationType = 'Manual' AND base_verification_type = 'Manual Monitoring'
				THEN
					CASE
						WHEN combinedStatus = 'Lemonade' THEN 'Digital Monitoring'
						ELSE base_verification_type
					END
			ELSE base_verification_type
		END AS F_VerificationType,
	q.combinedStatus,
	q.LocationId,
    q.LocationName,
    q.clientId,
    q.DealerName,
	q.`Name`,
    q.Email,
    q.MobileNumber,
    q.DealerId,
    q.DealerShipType,
	q.Dcid,
    q.Dealership,
    q.dealId,
	q.Vin,
	q.NewVin,
    q.CPI,
    q.TransactionType,
    q.Carrier,
    q.methodSent,
    q.OriginationSource,
    q.dateModified,
	q.LeaseStartDate,
	q.LeaseEndDate,
	q.VerificationDate,
    q.IntegrationPartner,
	q.isDeleted,
    q.activeStatus,
	q.PolicyStatus,
    q.AdequacyStatus,
    q.ApplicantLicenseStatus,
	q.quote_PolicyId,
	q.quote_QuoteId,
	q.looker_policy_date,
    q.looker_quote_date,    
    q.GroupLocationName,
    q.GroupLocationID,
    q.verificationSource,
	td.LEMONADE_PolicyId,
    td.LEMONADE_QuoteId,
	td.PolicyCreateDateTime,
    td.PolicyCreationMonth,
    CASE
		WHEN q.IsTestDealer = 1 THEN 'Test'
		WHEN IFNULL(q.ClientName, '') NOT LIKE '%Modives%'
			 AND IFNULL(DC.ContractName, '') NOT LIKE '%Test%'
			 AND IFNULL(DC.ContractName, '') NOT LIKE '%Venn%'
		THEN q.ClientName

		WHEN q.ClientName LIKE '%Modives%'
			 AND IFNULL(DC.ContractName, '') NOT LIKE '%Test%'
			 AND IFNULL(DC.ContractName, '') NOT LIKE '%Venn%'
		THEN q.LocationName

		ELSE DC.ContractName
	END AS ContractName,
    CASE 
		WHEN IFNULL(q.DCID,'')<>'' THEN q.DCID 
        ELSE q.DealerId 
	END AS PartnerID,
    CASE 
		WHEN q.base_verification_type IN ('Manual Verification','Digital Verification') THEN 'Verification'
        WHEN q.base_verification_type IN ('Manual Monitoring','Digital Monitoring') THEN 'Monitoring'
	END AS OverallVerificationAndMonitoring,
    CASE
		WHEN q.Vin IS NOT NULL THEN 'VIN-Yes'
        ELSE 'VIN-No'
	END AS F_VIN,
    CASE
		WHEN vti.FlowType = 1 THEN 'Digital Verification'
        WHEN vti.FlowType = 2 THEN 'Digital Monitoring'
        ELSE q.base_verification_type
	END AS F_VerificationType_Updated,
    CASE
		WHEN q.ApplicantLicenseStatus = 'Skipped' or q.ApplicantLicenseStatus IS  NULL or q.ApplicantLicenseStatus='' THEN 'License_No'
        WHEN q.ApplicantLicenseStatus IS NOT NULL OR q.ApplicantLicenseStatus <> '' THEN 'License_Yes'
	END AS F_ApplicantLicenseStatus
	
FROM quote_data_base q
LEFT JOIN transaction_data td ON td.VerificationId = q.SmrRenterVerificationId
LEFT JOIN `modives-analytics`.Dealer_Contracts DC ON q.DealerId = DC.DealerId
LEFT JOIN `modives-analytics`.stg_verificationtriggerinput vti 
		ON vti.VerificationReqId = q.AppId 
		AND DATE(vti.PolicyShellCreationDate) = DATE(q.VerificationDate)
),
/* Converted */
 ConvertedBaseData AS (
    SELECT DISTINCT
        vv.Id,
        vv.ParentVerificationId,
        vv.VerificationType,
        vv.MonitoringId,
        vv.CarrierName,
        avr.Lob,
        avr.isDeleted AS avrDeleted,
        vv.isDeleted AS vvDeleted,
        p.isDeleted  AS pDeleted,
        pvr.isDeleted AS pvrDeleted,
        pvr.Id AS AppId,
        pvr.StatusId,
        pvr.isCPI,
        p.DealerShipType,
        p.Id AS DealerId,
        p.PropertyName AS DealerName,
        loc.Id AS LocationId,
        loc.Name AS LocationName
    FROM `modives-analytics`.stg_verifications vv
    JOIN `modives-analytics`.stg_applicantverificationreqdetail avr
        ON avr.ApplicantVerificationReqGuidId = vv.SmrRenterVerificationId
    JOIN `modives-analytics`.stg_propertyverificationrequest pvr
        ON pvr.Id = avr.VerificationReqId
    JOIN `modives-analytics`.stg_property p
        ON p.Id = pvr.PropertyId
    JOIN `modives-analytics`.stg_location loc
        ON loc.Id = p.LocationId
),

AppCheck AS (
    SELECT DISTINCT
        AppId,
        SUM(VerificationType IN (1,2)) AS AutoCnt,
        SUM(VerificationType IN (0,3)) AS ManualCnt
    FROM ConvertedBaseData
    GROUP BY AppId
    HAVING AutoCnt > 0 AND ManualCnt > 0
),

Filtered AS (
    SELECT DISTINCT
    b.*
    FROM ConvertedBaseData b
    JOIN AppCheck a
      ON a.AppId = b.AppId
),

converted_data AS (

    SELECT DISTINCT
        CASE WHEN f.Lob = 1 THEN 'PROPERTY' ELSE 'AUTO' END AS Lob,
        cp.Pull_Id,
        f.MonitoringId,
        CASE f.DealerShipType
            WHEN 1 THEN 'BHPH'
            WHEN 2 THEN 'Franchise'
            WHEN 3 THEN 'Rental'
            WHEN 4 THEN 'Independent'
            WHEN 5 THEN 'Property'
            ELSE 'Dealership Not Found'
        END AS DealerShipType,
        'Canopy' AS IntegrationPartner,
        'Yes' AS Converted,
        f.Id AS VerificationId,
        CASE
            WHEN (IFNULL(f.avrDeleted,0)=1 OR IFNULL(f.vvDeleted,0)=1
               OR IFNULL(f.pDeleted,0)=1 OR IFNULL(f.pvrDeleted,0)=1)
            THEN 1 ELSE 0
        END AS isDeleted,
        CASE f.VerificationType
            WHEN 0 THEN 'Manual'
            WHEN 1 THEN 'Automatic'
            WHEN 2 THEN 'Monitoring-Automatic'
            WHEN 3 THEN 'Monitoring-Manual'
        END AS VerificationType,
        f.CarrierName,
        f.AppId,
        f.LocationName,
        f.LocationId,
        f.DealerId,
        f.DealerName,
        CASE WHEN f.StatusId = 3 THEN 'Cancelled' END AS Status,
        CASE WHEN f.isCPI = 1 THEN 'Yes' ELSE 'NO' END AS CPI
    FROM Filtered f
    JOIN `modives-analytics`.stg_Canopyapipayloadlogs cp
        ON cp.KeysOrTokens = f.Id

    UNION ALL

    SELECT DISTINCT
        CASE WHEN f.Lob = 1 THEN 'PROPERTY' ELSE 'AUTO' END,
        m.DataRequestId,
        f.MonitoringId,
        CASE f.DealerShipType
            WHEN 1 THEN 'BHPH'
            WHEN 2 THEN 'Franchise'
            WHEN 3 THEN 'Rental'
            WHEN 4 THEN 'Independent'
            WHEN 5 THEN 'Property'
            ELSE 'Dealership Not Found'
        END,
        'Measureone',
        'Yes',
        f.Id AS VerificationId,
        CASE
            WHEN (IFNULL(f.avrDeleted,0)=1 OR IFNULL(f.vvDeleted,0)=1
               OR IFNULL(f.pDeleted,0)=1 OR IFNULL(f.pvrDeleted,0)=1)
            THEN 1 ELSE 0
        END,
        CASE f.VerificationType
            WHEN 0 THEN 'Manual'
            WHEN 1 THEN 'Automatic'
            WHEN 2 THEN 'Monitoring-Automatic'
            WHEN 3 THEN 'Monitoring-Manual'
        END,
        f.CarrierName,
        f.AppId,
        f.LocationName,
        f.LocationId,
        f.DealerId,
        f.DealerName,
        CASE WHEN f.StatusId = 3 THEN 'Cancelled' END,
        CASE WHEN f.isCPI = 1 THEN 'Yes' ELSE 'NO' END
    FROM Filtered f
    JOIN `modives-analytics`.stg_measureoneapipayloadlogs m
        ON m.KeysOrTokens = f.Id

    UNION ALL

    SELECT DISTINCT
        CASE WHEN f.Lob = 1 THEN 'PROPERTY' ELSE 'AUTO' END,
        '' AS Pull_Id,
        f.MonitoringId,
        CASE f.DealerShipType
            WHEN 1 THEN 'BHPH'
            WHEN 2 THEN 'Franchise'
            WHEN 3 THEN 'Rental'
            WHEN 4 THEN 'Independent'
            WHEN 5 THEN 'Property'
            ELSE 'Dealership Not Found'
        END,
        '' AS IntegrationPartner,
        'Yes',
        f.Id AS VerificationId,
        CASE
            WHEN (IFNULL(f.avrDeleted,0)=1 OR IFNULL(f.vvDeleted,0)=1
               OR IFNULL(f.pDeleted,0)=1 OR IFNULL(f.pvrDeleted,0)=1)
            THEN 1 ELSE 0
        END,
        CASE f.VerificationType
            WHEN 0 THEN 'Manual'
            WHEN 1 THEN 'Automatic'
            WHEN 2 THEN 'Monitoring-Automatic'
            WHEN 3 THEN 'Monitoring-Manual'
        END,
        f.CarrierName,
        f.AppId,
        f.LocationName,
        f.LocationId,
        f.DealerId,
        f.DealerName,
        CASE WHEN f.StatusId = 3 THEN 'Cancelled' END,
        CASE WHEN f.isCPI = 1 THEN 'Yes' ELSE 'NO' END
    FROM Filtered f
    LEFT JOIN `modives-analytics`.stg_Canopyapipayloadlogs cp
        ON cp.KeysOrTokens = f.Id
    LEFT JOIN `modives-analytics`.stg_measureoneapipayloadlogs m
        ON m.KeysOrTokens = f.Id
    WHERE cp.KeysOrTokens IS NULL
      AND m.KeysOrTokens IS NULL
      AND m.KeysOrTokens IS NULL
)

SELECT DISTINCT
    d.Id AS Id,
    d.AppId  AS AppId,
    d.F_VerificationDate AS F_VerificationDate,
    d.F_VerificationDateMonthYear AS F_VerificationDateMonthYear,
    d.LookupActive AS LookupActive,
    d.Status AS Status,
    d.Lob AS Lob,
    d.Expedite AS Expedite,
    d.OverallVerificationType AS OverallVerificationType,
    d.F_VerificationType AS F_VerificationType,
    d.ClientName AS ClientName,
    d.ClientId AS ClientId,
    locpar.Name AS GroupLocationName,
    locpar.Id  AS GroupLocationID,
    d.LocationName AS LocationName,
    d.LocationId AS LocationId,
    d.DealerName AS DealerName,
    d.DealerId AS DealerId,
    d.DCID AS DCID,
    d.DealerShipType AS DealerShipType,
    d.`Name` AS Name,
    d.Email AS Email,
    d.MobileNumber AS MobileNumber,
    d.DealId AS DealId,
    d.Vin AS Vin,
    d.CPI AS CPI,
    d.TransactionType AS TransactionType,
    d.Carrier AS Carrier,
    d.MethodSent AS MethodSent,
    d.OriginationSource AS OriginationSource,
    d.DateModified  AS DateModified,
    d.LeaseStartDate AS LeaseStartDate,
    d.LeaseEndDate  AS LeaseEndDate,
    d.VerificationDate  AS VerificationDate,
    d.IntegrationPartner AS IntegrationPartner,
    d.IsDeleted AS IsDeleted,
    CASE
         WHEN d.F_VerificationType IN ('Digital Monitoring','Manual Monitoring')
               THEN ''
          ELSE TRIM(d.ApplicantLicenseStatus)
    END AS ApplicantLicenseStatus,
    d.ActiveStatus AS ActiveStatus,
    d.PolicyStatus AS PolicyStatus,
    d.AdequacyStatus AS AdequacyStatus,
    d.F_Status AS F_Status,
	d.CheckAnyTime AS CheckAnyTime,
    d.quote_PolicyId,
	d.quote_QuoteId,
    d.looker_policy_date,
	d.looker_quote_date,
    CASE 
		WHEN EXISTS (
			SELECT 1
			FROM converted_data cd
			WHERE cd.VerificationId = d.Id
		)
		THEN 'Yes'
		ELSE 'No'
	END AS Converted,
    C.UnitName,
    d.verificationSource,
	d.LEMONADE_PolicyId,
    d.LEMONADE_QuoteId,
    d.PolicyCreateDateTime,
    d.PolicyCreationMonth,
    d.ContractName,
    d.PartnerID,
    d.OverallVerificationAndMonitoring,
    d.F_VIN,
    d.F_VerificationType_Updated,
    d.F_ApplicantLicenseStatus,
    @LoadDate AS load_date

FROM deduped_data d
LEFT JOIN (
    SELECT hd1.*
    FROM `modives-analytics`.stg_hierarchydetails hd1
    WHERE hd1.ObjectType in (1,2) and
       EXISTS (
          SELECT 1
          FROM `modives-analytics`.stg_hierarchydetails hd2
          WHERE hd2.ObjectId = hd1.ParentObjectId
            AND hd2.IsHierarchyMaster = 1
      )
      AND NOT EXISTS (
          SELECT 1
          FROM `modives-analytics`.stg_hierarchydetails hd3
          WHERE hd3.ObjectId = hd1.ObjectId
            AND hd3.IsHierarchyMaster = 1
      )
) hd
    ON hd.ObjectId = d.LocationId
LEFT JOIN `modives-analytics`.stg_location locpar
    ON locpar.Id = hd.ParentObjectId
LEFT JOIN converted_data cd ON d.Id = cd.VerificationId
LEFT JOIN UnitCTE C ON C.AppId = d.AppId

WHERE d.rn = 1

UNION ALL
SELECT DISTINCT
	q.Id AS Id,
    q.AppId  AS AppId,
    DATE(q.CombinedDate) AS F_VerificationDate,
    q.F_VerificationDateMonthYear AS F_VerificationDateMonthYear,
    q.LookupActive AS LookupActive,
    q.Status AS Status,
    q.Lob AS Lob,
    q.Expedite AS Expedite,
    q.OverallVerificationType AS OverallVerificationType,
    q.F_VerificationType AS F_VerificationType,
    q.ClientName AS ClientName,
    q.ClientId AS ClientId,
    q.GroupLocationName AS GroupLocationName,
    q.GroupLocationID AS GroupLocationID,
    q.LocationName AS LocationName,
    q.LocationId AS LocationId,
    q.DealerName AS DealerName,
    q.DealerId AS DealerId,
    q.DCID AS DCID,
    q.DealerShipType AS DealerShipType,
    q.`Name` AS Name,
    q.Email AS Email,
    q.MobileNumber AS MobileNumber,
    q.DealId AS DealId,
    q.Vin AS Vin,
    q.CPI AS CPI,
    q.TransactionType  AS TransactionType,
    q.Carrier AS Carrier,
    q.MethodSent AS MethodSent,
    q.OriginationSource  AS OriginationSource,
    q.DateModified  AS DateModified,
    q.LeaseStartDate AS LeaseStartDate,
    q.LeaseEndDate  AS LeaseEndDate,
    q.VerificationDate  AS VerificationDate,
    q.IntegrationPartner AS IntegrationPartner,
    q.IsDeleted  AS IsDeleted,
    CASE
      WHEN q.F_VerificationType IN ('Digital Monitoring','Manual Monitoring')
      THEN ''
	  ELSE TRIM(q.ApplicantLicenseStatus)
    END AS ApplicantLicenseStatus,
    q.ActiveStatus AS ActiveStatus,
    q.PolicyStatus AS PolicyStatus,
    q.AdequacyStatus AS AdequacyStatus,
    q.CombinedStatus AS F_Status,
    NULL AS CheckAnyTime,
    q.quote_PolicyId AS quote_PolicyId,
	q.quote_QuoteId AS quote_QuoteId,
    q.looker_policy_date AS looker_policy_date,
	q.looker_quote_date AS looker_quote_date,
    'No' AS converted,
    C.UnitName,
    q.verificationSource,
	q.LEMONADE_PolicyId,
    q.LEMONADE_QuoteId,
	q.PolicyCreateDateTime,
	q.PolicyCreationMonth,
    q.ContractName,
    q.PartnerID,
    q.OverallVerificationAndMonitoring,
    q.F_VIN,
    q.F_VerificationType_Updated,
    q.F_ApplicantLicenseStatus,
    @LoadDate AS load_date

 FROM quote_data q
 LEFT JOIN UnitCTE C ON C.AppId = q.AppId;
