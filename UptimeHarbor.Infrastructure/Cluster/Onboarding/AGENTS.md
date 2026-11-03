# Адаптеры приглашений

InvitationAdministration сохраняет исходную защищённую Pending до transport; retry использует прежние байты, не генерирует новый код. ILocalAccountLogin проверяет свежий пароль и локальный lockout без изменения shared credentials. После await повторяется accepted authorization.

Успех журналирует OnboardingAudit только после соответствующего accepted результата в RevisionDispatcher. Секрет, verifier, URI, CSR, certificates, credentials, payload и raw exception не журналировать. Повтор известного OperationId не даёт второе success-событие.

OnboardingProtocol допускает только согласованный набор protocol/command/snapshot/SQL v2, без изменения конфигурации установки. Реальные CSR/PoP, issuer и online admission остаются закрытыми до реализации этапа 04; UnavailableEnrollmentAuthority нельзя заменять production double.
